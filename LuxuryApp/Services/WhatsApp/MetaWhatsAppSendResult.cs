using System.Net;

namespace LuxuryApp.Services.WhatsApp
{
    public sealed class MetaWhatsAppSendResult
    {
        public bool Success { get; init; }

        public string? MetaMessageId { get; init; }

        public HttpStatusCode? StatusCode { get; init; }

        public string? ErrorCode { get; init; }

        public string? ErrorType { get; init; }

        public int? ErrorSubcode { get; init; }

        public string? ErrorMessage { get; init; }

        public string? FbTraceId { get; init; }

        public bool ShouldRetry { get; init; }

        public string? ResponseBody { get; init; }

        public string? Endpoint { get; init; }

        public static MetaWhatsAppSendResult Succeeded(
            string metaMessageId,
            HttpStatusCode statusCode,
            string? responseBody,
            string? endpoint = null) =>
            new()
            {
                Success = true,
                MetaMessageId = metaMessageId,
                StatusCode = statusCode,
                ResponseBody = responseBody,
                Endpoint = endpoint
            };

        public static MetaWhatsAppSendResult Failed(
            string errorCode,
            string errorMessage,
            HttpStatusCode? statusCode = null,
            string? responseBody = null,
            string? errorType = null,
            int? errorSubcode = null,
            string? fbTraceId = null,
            bool shouldRetry = false,
            string? endpoint = null) =>
            new()
            {
                Success = false,
                ErrorCode = errorCode,
                ErrorType = errorType,
                ErrorSubcode = errorSubcode,
                ErrorMessage = errorMessage,
                FbTraceId = fbTraceId,
                ShouldRetry = shouldRetry,
                StatusCode = statusCode,
                ResponseBody = responseBody,
                Endpoint = endpoint
            };
    }
}
