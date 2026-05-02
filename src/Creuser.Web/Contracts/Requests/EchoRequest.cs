namespace Creuser.Web.Contracts.Requests;

public sealed record EchoRequest(string Message, int? Repeat);
