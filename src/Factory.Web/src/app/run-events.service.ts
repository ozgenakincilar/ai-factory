import { Injectable, OnDestroy, inject } from '@angular/core';
import { HttpTransportType, HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';
import { factoryConfig } from './factory.config';

export interface RunChanged { runId: string; status: string; }

// Real-time run change hints from /hubs/workflows. The hub accepts the token only in the
// Authorization header, so the browser uses long polling (WebSockets cannot send that header).
// Hints only: pages re-read authoritative state over HTTP.
@Injectable({ providedIn: 'root' })
export class RunEventsService implements OnDestroy {
  private readonly auth = inject(AuthService);
  private connection?: HubConnection;
  private watched?: string;
  readonly changed = new Subject<RunChanged>();

  async watch(projectId: string): Promise<void> {
    const connection = await this.connect();
    if (this.watched === projectId) return;
    if (this.watched) await connection.invoke('UnwatchProject', this.watched);
    this.watched = projectId;
    await connection.invoke('WatchProject', projectId);
  }

  private async connect(): Promise<HubConnection> {
    if (this.connection?.state === HubConnectionState.Connected) return this.connection;
    this.connection ??= this.build();
    if (this.connection.state === HubConnectionState.Disconnected) await this.connection.start();
    return this.connection;
  }

  private build(): HubConnection {
    const connection = new HubConnectionBuilder()
      .withUrl(factoryConfig.hubPath, { transport: HttpTransportType.LongPolling, accessTokenFactory: () => this.auth.accessToken() })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build();
    connection.on('runChanged', (change: RunChanged) => this.changed.next(change));
    // Group membership is per connection; re-join after a reconnect.
    connection.onreconnected(() => { if (this.watched) void connection.invoke('WatchProject', this.watched); });
    return connection;
  }

  ngOnDestroy(): void {
    void this.connection?.stop();
  }
}
