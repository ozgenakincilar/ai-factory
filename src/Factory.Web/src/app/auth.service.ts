import { Injectable, inject, signal } from '@angular/core';
import { AuthConfig, OAuthService } from 'angular-oauth2-oidc';
import { factoryConfig } from './factory.config';

// Plain HTTP is accepted only for a loopback development issuer; any other issuer must use HTTPS.
export function isLoopbackHttp(url: string): boolean {
  const parsed = new URL(url);
  return parsed.protocol === 'http:' && (parsed.hostname === '127.0.0.1' || parsed.hostname === 'localhost');
}

// Authorization Code + PKCE against Keycloak (public client factory-web). Tokens stay in session
// storage and are sent only to same-origin /api and /hubs requests.
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly oauth = inject(OAuthService);
  readonly signedIn = signal(false);
  readonly userName = signal<string | null>(null);
  readonly unavailable = signal(false);

  async initialize(): Promise<void> {
    const config: AuthConfig = {
      issuer: factoryConfig.issuer,
      clientId: factoryConfig.clientId,
      redirectUri: window.location.origin + '/',
      postLogoutRedirectUri: window.location.origin + '/',
      responseType: 'code',
      scope: 'openid',
      requireHttps: !isLoopbackHttp(factoryConfig.issuer),
      showDebugInformation: false,
      clearHashAfterLogin: true,
    };
    this.oauth.configure(config);
    try {
      await this.oauth.loadDiscoveryDocumentAndTryLogin();
      this.oauth.setupAutomaticSilentRefresh();
    } catch {
      // The page still renders and says sign-in is unavailable instead of staying blank.
      this.unavailable.set(true);
    }
    this.oauth.events.subscribe(() => this.refresh());
    this.refresh();
  }

  login(): void {
    this.oauth.initCodeFlow();
  }

  logout(): void {
    this.oauth.logOut();
  }

  accessToken(): string {
    return this.oauth.getAccessToken();
  }

  private refresh(): void {
    const valid = this.oauth.hasValidAccessToken();
    this.signedIn.set(valid);
    const claims = valid ? (this.oauth.getIdentityClaims() as Record<string, unknown> | null) : null;
    this.userName.set(typeof claims?.['preferred_username'] === 'string' ? (claims['preferred_username'] as string) : null);
  }
}
