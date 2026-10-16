import { provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideOAuthClient } from 'angular-oauth2-oidc';
import { routes } from './app.routes';
import { AuthService } from './auth.service';
import { factoryConfig } from './factory.config';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptorsFromDi()),
    // The access token is attached only to same-origin Factory API calls.
    provideOAuthClient({ resourceServer: { allowedUrls: [factoryConfig.apiPrefix], sendAccessToken: true } }),
    provideAppInitializer(() => inject(AuthService).initialize()),
  ],
};
