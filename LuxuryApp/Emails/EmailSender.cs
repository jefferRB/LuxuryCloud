using System.Net.Mail;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Services.Account;
using LuxuryApp.Services.DataBase;
using Microsoft.Extensions.Options;
using Resend;

namespace LuxuryApp.Emails
{
    public class EmailSender : EmailService
    {
        private readonly IConfiguration _config;
        private readonly IResend _resend;
        private readonly AccountEmailOptions _emailOpts;

        public EmailSender(IConfiguration config, IResend resend, IOptions<AccountEmailOptions> emailOptions)
        {
            _config = config;
            _resend = resend;
            _emailOpts = emailOptions.Value;
        }
        public async Task SendEmailAsync(string toEmail, string subject, string message)
        {
            await Execute(subject, message, toEmail);
        }

        public async Task Execute(string subject, string message, string toEmail)
        {
            var resendMessage = new EmailMessage();
            resendMessage.From = $"{_emailOpts.FromName} <{_emailOpts.FromEmail}>";
            resendMessage.To.Add(toEmail);
            resendMessage.Subject = subject;
            resendMessage.HtmlBody = message;


            await _resend.EmailSendAsync(resendMessage);

        }
        public async Task SendBulkEmailsAsync(List<ClientesModel> users, string subject, string template)
        {
            foreach (var user in users)
            {
                // Personaliza el mensaje para cada usuario
                string personalizedMessage = template
                    .Replace("@nombre@", user.Nombre)
                    .Replace("{email}", user.CorreoElectronico);

                // Envía el correo
                await SendEmailAsync(user.CorreoElectronico, subject, personalizedMessage);

                // Espera 600 ms antes de enviar el siguiente para cumplir rate limit
                await Task.Delay(600);
            }
        }

        public async Task SendBirthdayEmailAsync(List<ClientesModel> users, string subject, string template)
        {
            foreach (var user in users)
            {
                // Personaliza el mensaje para cada usuario
                string personalizedMessage = template
                    .Replace("@nombre@", user.Nombre)
                    .Replace("{email}", user.CorreoElectronico);

                // Envía el correo
                await SendEmailAsync(user.CorreoElectronico, subject, personalizedMessage);

                // Espera 600 ms antes de enviar el siguiente para cumplir rate limit
                await Task.Delay(600);
            }
        }
    }
}
