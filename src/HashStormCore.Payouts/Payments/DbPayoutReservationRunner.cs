using HashStormCore.Extensions;
using HashStormCore.Persistence;
using HashStormCore.Persistence.Model;

namespace HashStormCore.Payments;

public class DbPayoutReservationRunner : IPayoutReservationRunner
{
    public DbPayoutReservationRunner(IConnectionFactory connectionFactory,
        PayoutReservationService payoutReservationService)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.payoutReservationService = payoutReservationService ??
            throw new ArgumentNullException(nameof(payoutReservationService));
    }

    private readonly IConnectionFactory connectionFactory;
    private readonly PayoutReservationService payoutReservationService;

    public Task<CreatePayoutReservationResult> CreateReservationAsync(CreatePayoutReservationRequest request,
        CancellationToken ct)
    {
        return connectionFactory.RunTx((con, tx) =>
            payoutReservationService.CreateReservationAsync(con, tx, request, ct));
    }
}
