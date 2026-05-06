import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import type { HubConnection } from '@microsoft/signalr';

/**
 * Singleton SignalR client for the platform's `NotificationsHub`. The
 * hub broadcasts `notification(channel, payload)` events to clients
 * subscribed to a channel via `Subscribe(channel)`. Consumers (header
 * status component, future activity feeds, etc.) subscribe per channel
 * and get a typed handler called on every matching event.
 *
 * Handles its own connect, reconnect, and per-channel subscribe lifetime
 * — callers `subscribe(channel, handler)` and get an unsubscribe
 * function back. Reconnects re-issue every active subscription against
 * the new connection so handlers don't go silent after a drop.
 */
type Handler = (payload: unknown) => void;

class NotificationsClient {
  private connection: HubConnection | null = null;
  private connecting: Promise<void> | null = null;
  private readonly channelHandlers = new Map<string, Set<Handler>>();
  private readonly subscribedChannels = new Set<string>();

  /**
   * Connect to the hub if not already connected. Idempotent — multiple
   * callers awaiting the first connect share the same in-flight promise.
   */
  private async ensureConnected(): Promise<HubConnection> {
    if (this.connection?.state === HubConnectionState.Connected) return this.connection;
    if (this.connecting) {
      await this.connecting;
      return this.connection!;
    }
    this.connecting = (async () => {
      const conn = new HubConnectionBuilder()
        .withUrl('/hub/notifications')
        // Reconnect with a backoff schedule that's aggressive enough to
        // recover quickly from transient disconnects (Wi-Fi blips, page
        // suspend) but tapers so we don't hammer a downed server.
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .configureLogging(LogLevel.Warning)
        .build();

      conn.on('notification', (channel: string, payload: unknown) => {
        const handlers = this.channelHandlers.get(channel);
        if (!handlers) return;
        for (const h of handlers) {
          try {
            h(payload);
          } catch (err) {
            // Don't let one handler's bug kill the others.
            console.error('Notification handler error for', channel, err);
          }
        }
      });

      // After a reconnect, server-side group memberships are gone — the
      // hub holds them in memory keyed on the dropped connection id.
      // Re-issue every active subscription against the fresh connection
      // so handlers stay live.
      conn.onreconnected(() => {
        for (const channel of this.subscribedChannels) {
          conn.invoke('Subscribe', channel).catch((err) => {
            console.warn('Failed to re-subscribe to', channel, err);
          });
        }
      });

      await conn.start();
      this.connection = conn;
    })();
    try {
      await this.connecting;
    } finally {
      this.connecting = null;
    }
    return this.connection!;
  }

  /**
   * Subscribe to a hub channel. Returns an unsubscribe function. Multiple
   * handlers can subscribe to the same channel — they're stored in a
   * Set per channel and the SignalR-side `Subscribe(channel)` call only
   * fires once. The unsubscribe function is reference-counted: the
   * SignalR-side `Unsubscribe(channel)` only fires when the last
   * handler for the channel is removed.
   */
  async subscribe(channel: string, handler: Handler): Promise<() => Promise<void>> {
    const conn = await this.ensureConnected();
    let handlers = this.channelHandlers.get(channel);
    if (!handlers) {
      handlers = new Set();
      this.channelHandlers.set(channel, handlers);
    }
    handlers.add(handler);
    if (!this.subscribedChannels.has(channel)) {
      this.subscribedChannels.add(channel);
      await conn.invoke('Subscribe', channel);
    }
    return async () => {
      const set = this.channelHandlers.get(channel);
      if (!set) return;
      set.delete(handler);
      if (set.size === 0) {
        this.channelHandlers.delete(channel);
        this.subscribedChannels.delete(channel);
        if (this.connection?.state === HubConnectionState.Connected) {
          try {
            await this.connection.invoke('Unsubscribe', channel);
          } catch {
            // Best-effort — connection may have dropped. Server-side
            // group entry is keyed on connection id and will be GC'd
            // when the connection disposes.
          }
        }
      }
    };
  }
}

export const notifications = new NotificationsClient();
