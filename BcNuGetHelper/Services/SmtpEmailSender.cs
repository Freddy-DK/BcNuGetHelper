using System.Net;
using System.Net.Mail;

namespace BcNuGetHelper.Services;

/// <summary>
/// Sends notification e-mails over SMTP. Enabled only when all SMTP settings are present
/// (Smtp__Host, Smtp__Port, Smtp__User, Smtp__Password, Smtp__From); otherwise it is a no-op.
/// </summary>
public class SmtpEmailSender
{
    private readonly string? _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _password;
    private readonly string? _from;

    public SmtpEmailSender()
    {
        _host = Env("Smtp__Host");
        int.TryParse(Env("Smtp__Port"), out _port);
        _user = Env("Smtp__User");
        _password = Env("Smtp__Password");
        _from = Env("Smtp__From");
    }

    /// <summary>True when every SMTP setting is configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_host)
        && _port > 0
        && !string.IsNullOrEmpty(_user)
        && !string.IsNullOrEmpty(_password)
        && !string.IsNullOrEmpty(_from);

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(_from!),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(to);

        using var client = new SmtpClient(_host!, _port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_user, _password),
        };
        await client.SendMailAsync(message, ct);
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}
