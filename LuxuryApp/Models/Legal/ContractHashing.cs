using System.Security.Cryptography;
using System.Text;

namespace LuxuryApp.Models.Legal
{
    public static class ContractHashing
    {
        public static string ComputeSha256(string value)
        {
            ArgumentNullException.ThrowIfNull(value);

            var bytes = Encoding.UTF8.GetBytes(value);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
    }
}
