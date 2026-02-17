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

    [HttpGet("customers/lookup")]
    public async Task<ActionResult<OrganizationLookupResult>> LookupOrganization([FromQuery] long orgNumber, CancellationToken cancellationToken)
    {
        var result = await _repository.GetOrganizationLookupAsync(orgNumber, cancellationToken);
        if (result is null)
        {
            return NotFound(new { orgNumber, message = "Organization number not found in Registration_Account." });
        }

        return Ok(result);
    }

    [HttpGet("customers/deleted-flags")]
    public async Task<ActionResult<IReadOnlyList<DeletedCustomerFlagSummary>>> GetDeletedCustomerFlags(
        [FromQuery] string namePrefix = "XXXX",
        CancellationToken cancellationToken = default)
    {
        var results = await _repository.GetDeletedCustomerFlagSummariesAsync(namePrefix, cancellationToken);
        return Ok(results);
    }
}
