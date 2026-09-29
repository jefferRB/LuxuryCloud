namespace LuxuryApp.Services.PublicImages
{
    public sealed class PublicImageUploadException : Exception
    {
        public PublicImageUploadException(string message)
            : base(message)
        {
        }
    }
}
