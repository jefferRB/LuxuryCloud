using LuxuryApp.Models.DataBase;

namespace LuxuryApp.Services.DataBase
{
    public interface EmailService
    {
        Task SendBulkEmailsAsync(List<ClientesModel> users, string subject, string template);

    }
}
