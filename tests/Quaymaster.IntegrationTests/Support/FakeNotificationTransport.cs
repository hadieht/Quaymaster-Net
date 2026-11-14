using Quaymaster.Application.Notifications;
using Quaymaster.Domain.Notifications;

namespace Quaymaster.IntegrationTests.Support;

/// <summary>Records every outbound notification request / mail and answers with a scripted status (default 200).</summary>
public sealed class FakeNotificationTransport : INotificationTransport
{
    private readonly Lock _gate = new();
    private readonly List<OutboundHttpRequest> _requests = [];
    private readonly List<MailMessage> _mails = [];

    public Func<OutboundHttpRequest, int> Respond { get; set; } = _ => 200;

    public IReadOnlyList<OutboundHttpRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public IReadOnlyList<MailMessage> Mails
    {
        get
        {
            lock (_gate)
            {
                return [.. _mails];
            }
        }
    }

    public Task<int> SendRequestAsync(OutboundHttpRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        return Task.FromResult(Respond(request));
    }

    public Task SendMailAsync(MailMessage mail, CancellationToken ct)
    {
        lock (_gate)
        {
            _mails.Add(mail);
        }

        return Task.CompletedTask;
    }
}
