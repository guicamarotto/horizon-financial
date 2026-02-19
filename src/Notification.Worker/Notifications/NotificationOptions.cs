namespace Notification.Worker.Notifications;

public sealed class NotificationOptions
{
    public const string SectionName = "Notification";

    public bool FailOnRejectedEvents { get; set; }
}
