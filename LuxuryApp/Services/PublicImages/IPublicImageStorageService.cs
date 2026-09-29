namespace LuxuryApp.Services.PublicImages
{
    public sealed record PublicImageStoredObject(
        string StorageKey,
        string PublicUrl,
        string ContentType,
        long SizeBytes);

    public interface IPublicImageStorageService
    {
        Task<PublicImageStoredObject> UploadAsync(
            string storageKey,
            Stream content,
            string contentType,
            CancellationToken cancellationToken = default);

        Task<bool> TryDeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default);

        string BuildPublicUrl(string storageKey);

        bool IsValidStorageKey(string storageKey);
    }
}
