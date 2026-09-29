namespace LuxuryApp.Services.PublicImages
{
    public interface IUploadedFileSecurityScanner
    {
        Task ScanAsync(
            Stream content,
            string originalFileName,
            string contentType,
            CancellationToken cancellationToken = default);
    }

    public sealed class NoOpUploadedFileSecurityScanner : IUploadedFileSecurityScanner
    {
        public Task ScanAsync(
            Stream content,
            string originalFileName,
            string contentType,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
