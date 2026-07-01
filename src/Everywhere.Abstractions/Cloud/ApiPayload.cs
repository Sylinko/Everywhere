using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;

namespace Everywhere.Cloud;

/// <summary>
/// Standard structure for API error details.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class ApiError
{
    [Key(0)]
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [Key(1)]
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [Key(2)]
    [JsonPropertyName("upstream")]
    public UpstreamError? Upstream { get; set; }
}

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class UpstreamError
{
    [Key(0)]
    [JsonPropertyName("status")]
    public HttpStatusCode StatusCode { get; set; }

    [Key(1)]
    [JsonPropertyName("body")]
    public JsonDocument? Body { get; set; }
}

/// <summary>
/// Standard HTTP payload structure for API responses.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class ApiPayload
{
    /// <summary>
    /// Status code indicating success or error.
    /// </summary>
    [Key(0)]
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Details about the response error.
    /// </summary>
    [Key(1)]
    [JsonPropertyName("error")]
    public ApiError? Error { get; set; }

    /// <summary>
    /// Ensures that the response indicates success.
    /// </summary>
    /// <exception cref="HttpRequestException"></exception>
    public void EnsureSuccess()
    {
        if (!Success) throw new HttpRequestException(ToString());
    }

    public override string ToString() => JsonSerializer.Serialize(this);

    /// <summary>
    /// Deserializes the JSON payload from the HTTP response.
    /// </summary>
    /// <param name="response"></param>
    /// <param name="jsonSerializerOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="HttpRequestException"></exception>
    public static async Task<ApiPayload> FromHttpResponseJsonAsync(
        HttpResponseMessage response,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        await response.Content.ReadFromJsonAsync<ApiPayload>(jsonSerializerOptions, cancellationToken: cancellationToken) ??
        throw new HttpRequestException(HttpRequestError.InvalidResponse, statusCode: response.StatusCode);

    /// <summary>
    /// Ensures that the HTTP response indicates success and deserializes the JSON payload.
    /// </summary>
    /// <param name="response"></param>
    /// <param name="jsonSerializerOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="HttpRequestException">Thrown if the response indicates failure.</exception>
    public static async Task<ApiPayload> EnsureSuccessFromHttpResponseJsonAsync(
        HttpResponseMessage response,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var payload = await FromHttpResponseJsonAsync(response, jsonSerializerOptions, cancellationToken);
        if (!payload.Success) throw new HttpRequestException(payload.ToString(), null, statusCode: response.StatusCode);
        return payload;
    }
}

/// <summary>
/// Generic HTTP payload structure for API responses containing data of type T.
/// </summary>
/// <typeparam name="T"></typeparam>
public class ApiPayload<T> : ApiPayload
{
    [Key(2)]
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    public ApiPayload() { }

    public ApiPayload(T data)
    {
        Data = data;
    }

    /// <summary>
    /// Ensures that the status code indicates success and that Data is not null.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="HttpRequestException"></exception>
    public T EnsureData()
    {
        EnsureSuccess();
        return Data ?? throw new HttpRequestException($"{nameof(Data)} is null");
    }

    /// <summary>
    /// Deserializes the JSON payload from the HTTP response.
    /// </summary>
    /// <param name="response"></param>
    /// <param name="jsonSerializerOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="HttpRequestException"></exception>
    public static async new Task<ApiPayload<T>> FromHttpResponseJsonAsync(
        HttpResponseMessage response,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        await response.Content.ReadFromJsonAsync<ApiPayload<T>>(jsonSerializerOptions, cancellationToken: cancellationToken) ??
        throw new HttpRequestException(HttpRequestError.InvalidResponse, statusCode: response.StatusCode);

    /// <summary>
    /// Ensures that the HTTP response indicates success and deserializes the JSON payload.
    /// </summary>
    /// <param name="response"></param>
    /// <param name="jsonSerializerOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="HttpRequestException">Thrown if the response indicates failure.</exception>
    public static async new Task<ApiPayload<T>> EnsureSuccessFromHttpResponseJsonAsync(
        HttpResponseMessage response,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var payload = await FromHttpResponseJsonAsync(response, jsonSerializerOptions, cancellationToken);
        if (!payload.Success) throw new HttpRequestException(payload.ToString(), null, statusCode: response.StatusCode);
        return payload;
    }
}

[JsonSerializable(typeof(ApiPayload))]
public sealed partial class ApiPayloadJsonSerializerContext : JsonSerializerContext;