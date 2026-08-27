using Microsoft.AspNetCore.Mvc;
namespace Cluster.Controllers;

[ApiController]
[Route("[controller]/v1/")]
public class ApiController : Controller
{
    [HttpGet("")]
}
