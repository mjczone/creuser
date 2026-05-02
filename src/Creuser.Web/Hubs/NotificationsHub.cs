using Microsoft.AspNetCore.SignalR;

namespace Creuser.Web.Hubs;

public class NotificationsHub : Hub
{
    public Task Broadcast(string channel, object payload) =>
        Clients.Group(channel).SendAsync("notification", channel, payload);

    public Task Subscribe(string channel) => Groups.AddToGroupAsync(Context.ConnectionId, channel);

    public Task Unsubscribe(string channel) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, channel);
}
