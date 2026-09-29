namespace LuxuryApp.Services.BusinessTime
{
    public interface IBusinessDateTimeProvider
    {
        DateTime Now();
        DateTime Today();
        DateTimeOffset NowOffset();
    }
}
