namespace LuxuryApp.Models.DataBase
{
    public sealed class ClientesIndexViewModel
    {
        public IReadOnlyList<ClienteSummaryViewModel> Clientes { get; init; } = Array.Empty<ClienteSummaryViewModel>();
        public int PageNumber { get; init; }
        public int PageSize { get; init; }
        public int TotalCount { get; init; }
        public IReadOnlyList<int> PageSizeOptions { get; init; } = new[] { 10, 20, 50 };
        public int ClientesNuevosEsteMes { get; init; }
        public int VisitasEsteMes { get; init; }

        public int TotalPages => TotalCount == 0
            ? 1
            : (int)Math.Ceiling(TotalCount / (double)PageSize);

        public bool HasPreviousPage => PageNumber > 1;
        public bool HasNextPage => PageNumber < TotalPages;

        public int StartItem => TotalCount == 0
            ? 0
            : ((PageNumber - 1) * PageSize) + 1;

        public int EndItem => TotalCount == 0
            ? 0
            : Math.Min(PageNumber * PageSize, TotalCount);
    }
}
