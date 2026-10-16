import { Component, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from './auth.service';
import { DecisionDialog } from './decision-dialog';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, DecisionDialog],
  template: `
    <header>
      <a routerLink="/" class="brand">Universal AI Factory</a>
      @if (auth.signedIn()) {
        <span class="user">{{ auth.userName() }}</span>
        <button type="button" (click)="auth.logout()">Sign out</button>
      }
    </header>
    <main>
      @if (auth.signedIn()) {
        <router-outlet />
      } @else {
        <section class="welcome">
          <h1>Product Workspace</h1>
          <p>Sign in with your Factory account to see your projects and runs.</p>
          @if (auth.unavailable()) {
            <p class="error" role="alert">The sign-in service is unavailable. Try again later.</p>
          } @else {
            <button type="button" (click)="auth.login()">Sign in</button>
          }
        </section>
      }
    </main>
    @if (auth.signedIn()) { <app-decision-dialog /> }`,
  styles: `
    header { display: flex; align-items: center; gap: 16px; padding: 12px 24px; border-bottom: 1px solid var(--border); background: var(--surface); }
    .brand { font-weight: 600; color: var(--text); text-decoration: none; margin-right: auto; }
    .user { color: var(--muted); }
    main { max-width: 1100px; margin: 0 auto; padding: 24px; }
    button { padding: 6px 14px; border-radius: 6px; border: 1px solid var(--border); background: var(--accent); color: white; cursor: pointer; }
    .welcome { text-align: center; margin-top: 15vh; } .error { color: var(--danger); }`,
})
export class App {
  protected readonly auth = inject(AuthService);
}
