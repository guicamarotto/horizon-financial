const POLL_INTERVAL_MS = 1000;
const MAX_RECENT = 20;
const TIMELINE_STEP_MIN_MS = 1000;
const TIMELINE_STEP_MAX_MS = 2000;
const RUN_TEN_COUNT = 10;
const RUN_FAIL_COUNT = 4;

const state = {
  recentOrders: [],
  timeline: [],
  timelineTarget: [],
  timelineOrderId: null,
  playbackTimer: null,
  selectedOrderId: null,
  polling: false
};

const PIPELINE_STAGES = [
  {
    key: "ORDER_RECEIVED",
    title: "Order.Api",
    subtitle: "POST /orders + outbox",
    success: ["ORDER_RECEIVED", "OUTBOX_STORED"]
  },
  {
    key: "OUTBOX_PUBLISHED_KAFKA",
    title: "Outbox -> Kafka",
    subtitle: "orders.created",
    success: ["OUTBOX_PUBLISHED_KAFKA"]
  },
  {
    key: "RISK_CONSUMED_KAFKA",
    title: "Risk Engine",
    subtitle: "consome + decide",
    success: ["RISK_CONSUMED_KAFKA", "RISK_APPROVED", "RISK_REJECTED"]
  },
  {
    key: "COMMAND_PUBLISHED_RABBIT",
    title: "Rabbit Command",
    subtitle: "limits.commands",
    success: ["COMMAND_PUBLISHED_RABBIT"],
    skippedWhen: (ctx) => ctx.riskRejected
  },
  {
    key: "LIMIT_CONSUMED_RABBIT",
    title: "Limit Service",
    subtitle: "consome fila",
    success: ["LIMIT_CONSUMED_RABBIT"],
    skippedWhen: (ctx) => ctx.riskRejected
  },
  {
    key: "LIMIT_RESULT",
    title: "Limit -> Kafka",
    subtitle: "reserved/rejected",
    success: ["LIMIT_RESERVED_PUBLISHED_KAFKA", "LIMIT_REJECTED_PUBLISHED_KAFKA", "RISK_REJECTED"]
  },
  {
    key: "NOTIFICATION_CONSUMED_KAFKA",
    title: "Notification",
    subtitle: "consome evento final",
    success: ["NOTIFICATION_CONSUMED_KAFKA"]
  },
  {
    key: "NOTIFICATION_PERSISTED",
    title: "Persistencia",
    subtitle: "notifications.notifications",
    success: ["NOTIFICATION_PERSISTED"]
  }
];

const FAIL_STAGES = new Set(["DLQ_RABBIT_SENT", "DLQ_KAFKA_SENT"]);
const FAILURE_SYMBOLS = ["DLQ1"];

const elements = {
  runOnceBtn: document.getElementById("run-once-btn"),
  runTenBtn: document.getElementById("run-ten-btn"),
  runFailBtn: document.getElementById("run-fail-btn"),
  refreshRecentBtn: document.getElementById("refresh-recent-btn"),
  refreshDlqBtn: document.getElementById("refresh-dlq-btn"),
  replayRabbitBtn: document.getElementById("replay-rabbit-btn"),
  replayKafkaBtn: document.getElementById("replay-kafka-btn"),
  recentOrders: document.getElementById("recent-orders"),
  selectedOrder: document.getElementById("selected-order"),
  timeline: document.getElementById("timeline"),
  timelineCount: document.getElementById("timeline-count"),
  pipeline: document.getElementById("pipeline"),
  rabbitInflightCount: document.getElementById("rabbit-inflight-count"),
  kafkaInflightCount: document.getElementById("kafka-inflight-count"),
  rabbitCount: document.getElementById("rabbit-count"),
  kafkaCount: document.getElementById("kafka-count"),
  latestFailures: document.getElementById("latest-failures"),
  smokeResult: document.getElementById("smoke-result"),
  accountId: document.getElementById("account-id"),
  symbol: document.getElementById("symbol"),
  side: document.getElementById("side"),
  quantity: document.getElementById("quantity"),
  price: document.getElementById("price")
};

async function cleanupLegacyServiceWorkers() {
  if (!("serviceWorker" in navigator)) {
    return;
  }

  try {
    const registrations = await navigator.serviceWorker.getRegistrations();
    if (registrations.length === 0) {
      return;
    }

    await Promise.all(registrations.map((registration) => registration.unregister()));

    const alreadyReloaded = sessionStorage.getItem("lab_sw_cleanup_done") === "1";
    if (!alreadyReloaded) {
      sessionStorage.setItem("lab_sw_cleanup_done", "1");
      location.reload();
      return;
    }

    elements.smokeResult.textContent = "Service Worker antigo removido. Se ainda houver erro, limpe os dados do site.";
  } catch (error) {
    console.warn("Falha ao remover Service Worker legado:", error);
  }
}

async function apiGet(url) {
  const response = await fetch(url, { headers: { Accept: "application/json" } });
  if (!response.ok) {
    throw new Error(`GET ${url} -> ${response.status}`);
  }

  return response.json();
}

async function apiPost(url, body) {
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: body ? JSON.stringify(body) : "{}"
  });

  if (!response.ok) {
    throw new Error(`POST ${url} -> ${response.status}`);
  }

  return response.json();
}

function statusBadgeClass(status) {
  if (status === "LIMIT_RESERVED") {
    return "ok";
  }

  if (status === "REJECTED") {
    return "danger";
  }

  return "warn";
}

function formatDate(value) {
  if (!value) {
    return "-";
  }

  return new Date(value).toLocaleString("pt-BR", { hour12: false });
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function shortId(value) {
  if (!value) {
    return "-";
  }

  return value.length > 12 ? `${value.slice(0, 12)}...` : value;
}

function randomStepDelay() {
  const delta = TIMELINE_STEP_MAX_MS - TIMELINE_STEP_MIN_MS;
  return TIMELINE_STEP_MIN_MS + Math.floor(Math.random() * (delta + 1));
}

function stopTimelinePlayback() {
  if (state.playbackTimer) {
    window.clearTimeout(state.playbackTimer);
    state.playbackTimer = null;
  }
}

function resetTimelineForOrder(orderId) {
  stopTimelinePlayback();
  state.timeline = [];
  state.timelineTarget = [];
  state.timelineOrderId = orderId;
  renderTimeline();
}

function runTimelinePlayback() {
  if (state.playbackTimer) {
    return;
  }

  if (state.timeline.length >= state.timelineTarget.length) {
    return;
  }

  const nextIndex = state.timeline.length;
  const nextEvent = state.timelineTarget[nextIndex];
  state.timeline = [...state.timeline, nextEvent];
  renderTimeline();

  if (state.timeline.length >= state.timelineTarget.length) {
    return;
  }

  state.playbackTimer = window.setTimeout(() => {
    state.playbackTimer = null;
    runTimelinePlayback();
  }, randomStepDelay());
}

function renderRecentOrders() {
  const list = elements.recentOrders;
  list.innerHTML = "";

  if (state.recentOrders.length === 0) {
    list.innerHTML = "<li>Nenhuma ordem encontrada.</li>";
    return;
  }

  for (const order of state.recentOrders) {
    const li = document.createElement("li");
    if (order.orderId === state.selectedOrderId) {
      li.classList.add("selected");
    }

    li.innerHTML = `
      <div class="order-top">
        <strong>${escapeHtml(order.symbol)} ${escapeHtml(order.side)}</strong>
        <span class="badge ${statusBadgeClass(order.status)}">${escapeHtml(order.status)}</span>
      </div>
      <div class="order-id">${escapeHtml(order.orderId)}</div>
      <small>${escapeHtml(formatDate(order.createdAt))}</small>
    `;

    li.addEventListener("click", () => {
      if (state.selectedOrderId !== order.orderId) {
        resetTimelineForOrder(order.orderId);
      }
      state.selectedOrderId = order.orderId;
      renderRecentOrders();
      refreshSelectedOrderHeader();
      loadTimeline();
    });

    list.appendChild(li);
  }
}

function refreshSelectedOrderHeader() {
  if (!state.selectedOrderId) {
    elements.selectedOrder.textContent = "Nenhuma ordem selecionada";
    return;
  }

  const order = state.recentOrders.find((item) => item.orderId === state.selectedOrderId);
  if (!order) {
    elements.selectedOrder.textContent = `order_id=${state.selectedOrderId}`;
    return;
  }

  elements.selectedOrder.textContent = `order_id=${order.orderId} status=${order.status}`;
}

function timelineSet() {
  return new Set(state.timeline.map((item) => item.stage));
}

function renderPipeline() {
  const set = timelineSet();
  const ctx = {
    riskRejected: set.has("RISK_REJECTED"),
    hasFailure: state.timeline.some((entry) => FAIL_STAGES.has(entry.stage))
  };

  const stageState = PIPELINE_STAGES.map((stage) => {
    if (stage.skippedWhen?.(ctx)) {
      return "skipped";
    }

    if (stage.success.some((stageName) => set.has(stageName))) {
      return "success";
    }

    return "pending";
  });

  const activeIndex = stageState.findIndex((value) => value === "pending");
  if (activeIndex >= 0 && !ctx.hasFailure) {
    stageState[activeIndex] = "active";
  }

  if (ctx.hasFailure) {
    const failIndex = stageState.findIndex((value) => value === "active" || value === "pending");
    if (failIndex >= 0) {
      stageState[failIndex] = "failed";
    }
  }

  elements.pipeline.innerHTML = "";
  PIPELINE_STAGES.forEach((stage, idx) => {
    const card = document.createElement("article");
    card.className = `stage ${stageState[idx]}`;
    card.innerHTML = `
      <div class="stage-title">${stage.title}</div>
      <div class="stage-sub">${stage.subtitle}</div>
    `;
    elements.pipeline.appendChild(card);
  });
}

function renderTimeline() {
  elements.timeline.innerHTML = "";
  const total = state.timelineTarget.length;
  elements.timelineCount.textContent = total > 0
    ? `${state.timeline.length}/${total} eventos`
    : `${state.timeline.length} eventos`;

  if (state.timeline.length === 0) {
    elements.timeline.innerHTML = "<li>Nenhum evento para esta ordem ainda.</li>";
    renderPipeline();
    return;
  }

  for (const event of state.timeline) {
    const li = document.createElement("li");
    const badge = FAIL_STAGES.has(event.stage) ? "danger" : "ok";
    li.innerHTML = `
      <div class="order-top">
        <strong>${escapeHtml(event.stage)}</strong>
        <span class="badge ${badge}">${escapeHtml(event.service)}</span>
      </div>
      <div class="meta">
        ${escapeHtml(formatDate(event.createdAt))} |
        broker=${escapeHtml(event.broker || "NONE")} |
        canal=${escapeHtml(event.channel || "-")} |
        message_id=${escapeHtml(shortId(event.messageId))}
      </div>
      ${event.payloadSnippet ? `<small>${escapeHtml(event.payloadSnippet)}</small>` : ""}
    `;

    elements.timeline.appendChild(li);
  }

  renderPipeline();
}

function renderDlq(overview) {
  elements.rabbitInflightCount.textContent = overview.rabbitInFlightCount ?? 0;
  elements.kafkaInflightCount.textContent = overview.kafkaInFlightCount ?? 0;
  elements.rabbitCount.textContent = overview.rabbitPendingCount ?? 0;
  elements.kafkaCount.textContent = overview.kafkaDlqBacklogEstimate ?? 0;

  const failures = overview.latestFailures || [];
  elements.latestFailures.innerHTML = "";

  if (failures.length === 0) {
    elements.latestFailures.innerHTML = "<li>Sem falhas recentes no journal.</li>";
    return;
  }

  for (const failure of failures.slice(0, 12)) {
    const li = document.createElement("li");
    li.innerHTML = `
      <div class="order-top">
        <strong>${escapeHtml(failure.stage)}</strong>
        <span class="badge danger">${escapeHtml(failure.service)}</span>
      </div>
      <div class="meta">
        ${escapeHtml(formatDate(failure.createdAt))} |
        order_id=${escapeHtml(shortId(failure.orderId || ""))}
      </div>
      ${failure.payloadSnippet ? `<small>${escapeHtml(failure.payloadSnippet)}</small>` : ""}
    `;
    elements.latestFailures.appendChild(li);
  }
}

async function loadRecentOrders() {
  const previousSelectedOrderId = state.selectedOrderId;
  const recent = await apiGet(`/lab/api/orders/recent?limit=${MAX_RECENT}`);
  state.recentOrders = Array.isArray(recent) ? recent : [];

  const latest = state.recentOrders[0];
  if (!state.selectedOrderId && latest) {
    state.selectedOrderId = latest.orderId;
  }

  if (state.selectedOrderId !== previousSelectedOrderId) {
    resetTimelineForOrder(state.selectedOrderId);
  }

  renderRecentOrders();
  refreshSelectedOrderHeader();
}

async function loadTimeline() {
  if (!state.selectedOrderId) {
    resetTimelineForOrder(null);
    return;
  }

  const orderId = state.selectedOrderId;
  const fetchedTimeline = await apiGet(`/lab/api/orders/${orderId}/timeline?limit=500`);
  if (state.selectedOrderId !== orderId) {
    return;
  }

  if (state.timelineOrderId !== orderId) {
    resetTimelineForOrder(orderId);
  }

  state.timelineTarget = Array.isArray(fetchedTimeline) ? fetchedTimeline : [];

  if (state.timeline.length > state.timelineTarget.length) {
    state.timeline = state.timelineTarget.slice();
  }

  renderTimeline();
  runTimelinePlayback();
}

async function loadDlq() {
  const overview = await apiGet("/lab/api/dlq/overview");
  renderDlq(overview);
}

function getSmokeRequest() {
  const accountValue = elements.accountId.value.trim();
  return {
    accountId: accountValue.length > 0 ? accountValue : null,
    symbol: elements.symbol.value.trim() || "PETR4",
    side: elements.side.value,
    quantity: Number(elements.quantity.value || "100"),
    price: Number(elements.price.value || "32.15")
  };
}

function delay(ms) {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}

function setRunButtonsDisabled(value) {
  elements.runOnceBtn.disabled = value;
  elements.runTenBtn.disabled = value;
  elements.runFailBtn.disabled = value;
}

async function runOrders(count, mode) {
  setRunButtonsDisabled(true);
  elements.smokeResult.textContent = "Executando...";

  try {
    const createdOrders = [];

    for (let i = 0; i < count; i++) {
      const request = getSmokeRequest();
      if (count > 1) {
        request.accountId = null;
      }

      if (mode === "fail") {
        request.symbol = FAILURE_SYMBOLS[i % FAILURE_SYMBOLS.length];
      }

      const result = await apiPost("/lab/api/smoke/run", request);
      createdOrders.push(result.orderId);
      elements.smokeResult.textContent = `Executando ${i + 1}/${count}... ultima ordem=${result.orderId}`;

      // Pequena pausa para criar lotes sem esmagar a API e manter a demo legivel.
      if (count > 1) {
        await delay(120);
      }
    }

    const lastOrderId = createdOrders[createdOrders.length - 1];
    if (lastOrderId) {
      state.selectedOrderId = lastOrderId;
      resetTimelineForOrder(lastOrderId);
    }

    if (mode === "fail") {
      elements.smokeResult.textContent = `Falhas disparadas: ${count} ordens (simbolo ${FAILURE_SYMBOLS[0]}). DLQ deve aparecer em poucos segundos.`;
    } else {
      elements.smokeResult.textContent = `Execucao concluida: ${count} ordem(ns). Ultima ordem=${lastOrderId}`;
    }

    await refreshAll();
  } catch (error) {
    elements.smokeResult.textContent = `Erro ao executar lote: ${error.message}`;
  } finally {
    setRunButtonsDisabled(false);
  }
}

async function replayRabbit() {
  elements.replayRabbitBtn.disabled = true;
  try {
    await apiPost("/lab/api/dlq/replay/rabbit?count=50");
    await refreshAll();
  } finally {
    elements.replayRabbitBtn.disabled = false;
  }
}

async function replayKafka() {
  elements.replayKafkaBtn.disabled = true;
  try {
    await apiPost("/lab/api/dlq/replay/kafka?count=50");
    await refreshAll();
  } finally {
    elements.replayKafkaBtn.disabled = false;
  }
}

async function runOnce() {
  await runOrders(1, "normal");
}

async function runTen() {
  await runOrders(RUN_TEN_COUNT, "normal");
}

async function runFailures() {
  await runOrders(RUN_FAIL_COUNT, "fail");
}

async function refreshAll() {
  if (state.polling) {
    return;
  }

  state.polling = true;
  try {
    await loadRecentOrders();
    await Promise.all([loadTimeline(), loadDlq()]);
  } catch (error) {
    elements.smokeResult.textContent = `Falha no polling: ${error.message}`;
  } finally {
    state.polling = false;
  }
}

function wireActions() {
  elements.runOnceBtn.addEventListener("click", runOnce);
  elements.runTenBtn.addEventListener("click", runTen);
  elements.runFailBtn.addEventListener("click", runFailures);
  elements.refreshRecentBtn.addEventListener("click", loadRecentOrders);
  elements.refreshDlqBtn.addEventListener("click", loadDlq);
  elements.replayRabbitBtn.addEventListener("click", replayRabbit);
  elements.replayKafkaBtn.addEventListener("click", replayKafka);
}

wireActions();
cleanupLegacyServiceWorkers().finally(() => {
  refreshAll();
  window.setInterval(refreshAll, POLL_INTERVAL_MS);
});
