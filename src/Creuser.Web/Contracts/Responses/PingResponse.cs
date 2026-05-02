namespace Creuser.Web.Contracts.Responses;

public sealed record PingResponse(string Message, DateTimeOffset ServerTime);
