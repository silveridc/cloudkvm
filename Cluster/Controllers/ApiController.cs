using Microsoft.AspNetCore.Mvc;

namespace Cluster.Controllers;

class ApiController : ControllerBase
{
    public IActionResult Index()
    {
        return Ok(new
        {
            status = 200,
            message = "Success",
            data = Array.Empty<object>(),
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }
}