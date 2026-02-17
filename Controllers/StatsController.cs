using Microsoft.AspNetCore.Mvc;

namespace DigCfoWebApi.Controllers;

[ApiController]
[Route("stats")]
public class StatsController : ControllerBase
{
    private readonly StatsRepository _repository;

    public StatsController(StatsRepository repository)
    {
        _repository = repository;
    }

    [HttpGet]
    public async Task<ActionResult<StatsSummary>> GetSummary(CancellationToken cancellationToken)
    {
        var summary = await _repository.GetSummaryAsync(cancellationToken);
        return Ok(summary);
    }

    [HttpGet("customers")]
    public async Task<ActionResult<IReadOnlyList<CustomerOverview>>> GetCustomers(CancellationToken cancellationToken)
    {
        var customers = await _repository.GetCustomersAsync(cancellationToken);
        return Ok(customers);
    }
}
