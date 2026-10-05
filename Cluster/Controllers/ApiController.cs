using Microsoft.AspNetCore.Mvc;

namespace Cluster.Controllers;
//[ApiController]
/// <summary>根路径健康检查端点。</summary>
class ApiController : ControllerBase
{
    [HttpGet("/")]
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