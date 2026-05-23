using HashStormCore.Persistence.Model;

namespace HashStormCore.Payments;

public interface IPayoutReservationRunner
{
    Task<CreatePayoutReservationResult> CreateReservationAsync(CreatePayoutReservationRequest request,
        CancellationToken ct);
}
