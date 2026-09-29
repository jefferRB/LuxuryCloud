using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class S3CompatiblePublicImageStorageServiceTests
    {
        [Theory]
        [InlineData("https://accountid.r2.cloudflarestorage.com", "accountid.r2.cloudflarestorage.com", true)]
        [InlineData("https://accountid.r2.cloudflarestorage.com/", "accountid.r2.cloudflarestorage.com", true)]
        [InlineData("http://localhost:9000", "localhost", false)]
        [InlineData("accountid.r2.cloudflarestorage.com", "accountid.r2.cloudflarestorage.com", true)]
        public void ResolveEndpoint_NormalizesToHostAndDetectsSsl(string endpoint, string expectedHost, bool expectedSsl)
        {
            var (host, secure) = S3CompatiblePublicImageStorageService.ResolveEndpoint(endpoint);

            Assert.Equal(expectedHost, host);
            Assert.Equal(expectedSsl, secure);
        }

        [Fact]
        public void BuildPublicUrl_ValidKey_UsesPublicBaseUrl()
        {
            var service = CreateService();
            var key = $"tenants/{Guid.NewGuid():N}/public-page/location/{Guid.NewGuid():N}.webp";

            var url = service.BuildPublicUrl(key);

            Assert.Equal($"https://media.luxurycloud.app/{key}", url);
        }

        [Fact]
        public async Task UploadAsync_InvalidStorageKey_ThrowsBeforeAnyNetworkCall()
        {
            var service = CreateService();
            using var content = new MemoryStream(new byte[] { 1, 2, 3 });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UploadAsync("../evil/path.webp", content, "image/webp"));
        }

        [Fact]
        public async Task EnsureSeekableAsync_NonSeekableStream_CopiesToSeekableMemoryStream()
        {
            var payload = Encoding.UTF8.GetBytes("contenido-no-seekable");
            await using var nonSeekable = new NonSeekableStream(payload);

            var (stream, created) = await S3CompatiblePublicImageStorageService.EnsureSeekableAsync(
                nonSeekable,
                CancellationToken.None);

            Assert.True(created);
            Assert.True(stream.CanSeek);
            Assert.Equal(0, stream.Position);
            Assert.Equal(payload.Length, stream.Length);

            using var reader = new MemoryStream();
            await stream.CopyToAsync(reader);
            Assert.Equal(payload, reader.ToArray());

            await stream.DisposeAsync();
        }

        [Fact]
        public async Task EnsureSeekableAsync_SeekableStream_ReusesAndResetsPosition()
        {
            using var seekable = new MemoryStream(new byte[] { 9, 8, 7 });
            seekable.Position = 2;

            var (stream, created) = await S3CompatiblePublicImageStorageService.EnsureSeekableAsync(
                seekable,
                CancellationToken.None);

            Assert.False(created);
            Assert.Same(seekable, stream);
            Assert.Equal(0, stream.Position);
        }

        [Fact]
        public void Source_UsesMinioAndMarker_AndNoAwsSdkTypes()
        {
            var source = File.ReadAllText(ProjectPath(
                "Services", "PublicImages", "S3CompatiblePublicImageStorageService.cs"));

            // Usa Minio y el marcador requerido.
            Assert.Contains("R2_MINIO_UPLOAD_ACTIVE", source);
            Assert.Contains("Minio", source);
            Assert.Contains("PutObjectArgs", source);
            Assert.Contains("RemoveObjectArgs", source);

            // Ya no usa AWSSDK.S3.
            Assert.DoesNotContain("AmazonS3Client", source);
            Assert.DoesNotContain("PutObjectRequest", source);
            Assert.DoesNotContain("using Amazon", source);
        }

        private static S3CompatiblePublicImageStorageService CreateService() =>
            new(
                Options.Create(new S3StorageOptions
                {
                    Endpoint = "https://accountid.r2.cloudflarestorage.com",
                    Region = "auto",
                    BucketName = "luxurycloud-public-assets-prod",
                    AccessKey = "test-access",
                    SecretKey = "test-secret",
                    PublicBaseUrl = "https://media.luxurycloud.app/"
                }),
                NullLogger<S3CompatiblePublicImageStorageService>.Instance);

        private static string ProjectPath(params string[] parts)
        {
            return TestProjectPaths.ProjectPath(parts);
        }

        // Stream forward-only para simular un contenido no seekable (ej. red).
        private sealed class NonSeekableStream : Stream
        {
            private readonly MemoryStream _inner;

            public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
