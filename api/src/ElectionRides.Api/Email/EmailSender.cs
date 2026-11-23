namespace ElectionRides.Api.Email;

public record EmailMessage(string To, string Subject, string Body);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// Development sender: does not send anything. Writes each email to the log and keeps the last
/// ones in memory, so codes and links can be read locally and in tests. Amazon SES replaces it in the cloud.
/// </summary>
public class DevEmailSender(ILogger<DevEmailSender> log) : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<EmailMessage> Sent { get { lock (_gate) return _sent.ToList(); } }

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _sent.Add(message);
            if (_sent.Count > 100) _sent.RemoveAt(0);
        }
        log.LogWarning("DEV EMAIL to {To}: {Subject}\n{Body}", message.To, message.Subject, message.Body);
        return Task.CompletedTask;
    }
}
