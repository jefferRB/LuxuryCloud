namespace LuxuryApp.Tests.Support
{
    internal static class TestProjectPaths
    {
        public static string RepositoryRoot { get; } = FindRepositoryRoot();

        public static string ProjectPath(params string[] parts) =>
            Path.Combine(new[] { RepositoryRoot }.Concat(parts).ToArray());

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LuxuryApp.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No se encontro LuxuryApp.csproj subiendo desde {AppContext.BaseDirectory}.");
        }
    }
}
