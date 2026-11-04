using System.Net;
using System.Net.Mail;
using Planna.Application.Identity;

namespace Planna.Infrastructure.Identity;

public sealed class SmtpLoginCodeSender(
    string host, int port, string username, string password, string from) : ILoginCodeSender
{
    public async Task SendAsync(string email, string code, CancellationToken ct)
    {
        using var message = new MailMessage(from, email)
        {
            Subject = "Planna 登录验证码",
            Body = $"验证码：{code}。10 分钟内有效；如果不是你本人操作，请忽略。"
        };
        using var smtp = new SmtpClient(host, port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(username, password)
        };
        await smtp.SendMailAsync(message, ct);
    }
}

public sealed class UnavailableLoginCodeSender : ILoginCodeSender
{
    public Task SendAsync(string email, string code, CancellationToken ct) =>
        throw new InvalidOperationException("SMTP is not configured.");
}
