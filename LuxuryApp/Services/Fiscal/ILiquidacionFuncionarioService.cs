using LuxuryApp.Models.Fiscal;

namespace LuxuryApp.Services.Fiscal
{
    /// <summary>
    /// Motor de liquidación del colaborador. Calcula la base de comisión, el IVA de la factura
    /// del colaborador independiente (cuando aplica) y el total a pagar. Sin estado / testeable.
    /// </summary>
    public interface ILiquidacionFuncionarioService
    {
        LiquidacionColaboradorResult Liquidar(LiquidacionColaboradorInput input);
    }
}
