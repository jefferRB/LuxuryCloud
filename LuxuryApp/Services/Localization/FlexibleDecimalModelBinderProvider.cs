using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Services.Localization
{
    public sealed class FlexibleDecimalModelBinderProvider : IModelBinderProvider
    {
        private static readonly IModelBinder Binder = new FlexibleDecimalModelBinder();

        public IModelBinder? GetBinder(ModelBinderProviderContext context)
        {
            var modelType = context.Metadata.UnderlyingOrModelType;
            return modelType == typeof(decimal) ? Binder : null;
        }
    }
}
