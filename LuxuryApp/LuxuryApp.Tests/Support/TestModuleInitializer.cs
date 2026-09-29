using System.Runtime.CompilerServices;

namespace LuxuryApp.Tests.Support
{
    internal static class TestModuleInitializer
    {
        /// <summary>
        /// La licencia de QuestPDF se configura en <c>Program.cs</c>, que los tests no ejecutan.
        /// Sin esto, generar un PDF lanza excepción de licencia. Mismo valor que producción.
        /// </summary>
        [ModuleInitializer]
        internal static void Initialize()
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }
    }
}
