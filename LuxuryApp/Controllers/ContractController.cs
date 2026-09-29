using System.Security.Claims;
using LuxuryApp.Filters;
using LuxuryApp.Models.Legal;
using LuxuryApp.Services.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers
{
    public sealed class ContractController : Controller
    {
        private readonly IContractService _contractService;

        public ContractController(IContractService contractService)
        {
            _contractService = contractService;
        }

        [HttpGet]
        [HttpHead]
        [AllowAnonymous]
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var activeContract = await _contractService.GetActiveContractAsync(cancellationToken);

            return View(MapToPageViewModel(activeContract));
        }

        [HttpGet]
        // Excluida del gate de MFA: si un superadmin tiene contrato pendiente Y MFA pendiente,
        // el contrato debe poder completarse primero (evita ping-pong entre ambos gates).
        [AllowWithoutMfaEnrollment]
        public async Task<IActionResult> Reaccept(
            string? returnurl = null,
            CancellationToken cancellationToken = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Challenge();
            }

            var safeReturnUrl = Url.IsLocalUrl(returnurl)
                ? returnurl!
                : Url.Content("~/") ?? "/";

            var status = await _contractService.GetAcceptanceStatusAsync(userId, cancellationToken);
            if (!status.BlocksApplicationAccess)
            {
                return LocalRedirect(safeReturnUrl);
            }

            return View(MapToReacceptViewModel(status.ActiveDocument, safeReturnUrl));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [AllowWithoutMfaEnrollment]
        public async Task<IActionResult> Reaccept(
            ContractReacceptViewModel model,
            CancellationToken cancellationToken = default)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Challenge();
            }

            var safeReturnUrl = Url.IsLocalUrl(model.ReturnUrl)
                ? model.ReturnUrl
                : Url.Content("~/") ?? "/";

            var submittedContractDocumentId = model.ContractDocumentId;
            var activeDocument = await _contractService.GetActiveContractAsync(cancellationToken);
            var currentModel = MapToReacceptViewModel(activeDocument, safeReturnUrl);

            model.HasActiveDocument = currentModel.HasActiveDocument;
            model.Title = currentModel.Title;
            model.VersionNumber = currentModel.VersionNumber;
            model.EffectiveFromUtc = currentModel.EffectiveFromUtc;
            model.ContentHtml = currentModel.ContentHtml;
            model.ReturnUrl = currentModel.ReturnUrl;
            NormalizeContractAcceptance(model);

            if (!model.HasActiveDocument)
            {
                ModelState.AddModelError(string.Empty, "No hay un contrato vigente configurado. Contacta soporte antes de continuar.");
            }

            if (!ModelState.IsValid)
            {
                model.ContractDocumentId = currentModel.ContractDocumentId;
                return View(model);
            }

            try
            {
                await _contractService.RegisterAcceptanceAsync(
                    userId,
                    submittedContractDocumentId,
                    ContractAcceptanceSources.Reaccept,
                    ContractRequestMetadataResolver.ResolveClientIp(HttpContext),
                    ContractRequestMetadataResolver.ResolveUserAgent(HttpContext),
                    cancellationToken);

                return LocalRedirect(safeReturnUrl);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                model.ContractDocumentId = currentModel.ContractDocumentId;
                return View(model);
            }
        }

        private static ContractPageViewModel MapToPageViewModel(ContractDocument? activeDocument)
        {
            if (activeDocument is null)
            {
                return new ContractPageViewModel
                {
                    HasActiveDocument = false
                };
            }

            return new ContractPageViewModel
            {
                HasActiveDocument = true,
                ContractDocumentId = activeDocument.Id,
                Title = activeDocument.Title,
                VersionNumber = activeDocument.VersionNumber,
                EffectiveFromUtc = activeDocument.EffectiveFromUtc,
                ContentHtml = activeDocument.ContentHtml
            };
        }

        private static ContractReacceptViewModel MapToReacceptViewModel(
            ContractDocument? activeDocument,
            string returnUrl)
        {
            if (activeDocument is null)
            {
                return new ContractReacceptViewModel
                {
                    HasActiveDocument = false,
                    ReturnUrl = returnUrl
                };
            }

            return new ContractReacceptViewModel
            {
                HasActiveDocument = true,
                ContractDocumentId = activeDocument.Id,
                Title = activeDocument.Title,
                VersionNumber = activeDocument.VersionNumber,
                EffectiveFromUtc = activeDocument.EffectiveFromUtc,
                ContentHtml = activeDocument.ContentHtml,
                ReturnUrl = returnUrl
            };
        }

        private void NormalizeContractAcceptance(ContractReacceptViewModel model)
        {
            var fieldName = nameof(ContractReacceptViewModel.AcceptCurrentContract);
            model.AcceptCurrentContract = Request.HasFormContentType
                ? ContractAcceptanceBindingHelper.IsAccepted(Request.Form, fieldName)
                : model.AcceptCurrentContract;

            ModelState.Remove(fieldName);

            if (!model.AcceptCurrentContract)
            {
                ModelState.AddModelError(
                    fieldName,
                    "Debes aceptar el contrato vigente para continuar.");
            }
        }
    }
}
