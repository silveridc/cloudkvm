using Microsoft.AspNetCore.Mvc;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Control.Model.Response;

/// <summary>统一 API 响应包装：状态码、消息、数据与时间戳。</summary>
public sealed class Response<T>(int status, string message, T? data = default)
{
    public int Status { get; set; } = status;
    public string Message { get; set; } = message;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public T? Data { get; set; } = data;
    public long Time { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>构建统一响应 JSON 的辅助方法集。</summary>
public static class Response
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task WriteAsync(HttpContext context, string message, ResponseCode status, int httpStatusCode)
    {
        context.Response.Clear();
        context.Response.StatusCode = httpStatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new Response<object?>((int)status, message), _jsonOptions);
    }

    public static JsonResult Success<T>(string message, T data, int httpStatusCode = StatusCodes.Status200OK)
    {
        return new JsonResult(new Response<T>((int)ResponseCode.Success, message, data)) { StatusCode = httpStatusCode };
    }

    public static JsonResult Success(string message, int httpStatusCode = StatusCodes.Status200OK)
    {
        return new JsonResult(new Response<object?>((int)ResponseCode.Success, message)) { StatusCode = httpStatusCode };
    }

    public static JsonResult Fail(string message, ResponseCode status, int httpStatusCode)
    {
        return new JsonResult(new Response<object?>((int)status, message)) { StatusCode = httpStatusCode };
    }

    public static JsonResult Fail<T>(string message, T data, ResponseCode status, int httpStatusCode)
    {
        return new JsonResult(new Response<T>((int)status, message, data)) { StatusCode = httpStatusCode };
    }
}
