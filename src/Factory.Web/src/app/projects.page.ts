import { Component, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { WorkspaceState } from './workspace.state';

@Component({
  selector: 'app-projects-page',
  imports: [RouterLink],
  template: `
    <h1>Projects</h1>
    @if (state.error()) { <p class="error" role="alert">Projects could not be loaded.</p> }
    @if (state.projects().length === 0) {
      <p>You are not a member of any project yet.</p>
    } @else {
      <ul class="projects">
        @for (project of state.projects(); track project.projectId) {
          <li>
            <a [routerLink]="['/projects', project.projectId]">{{ project.projectId }}</a>
            <span class="meta">{{ project.organizationId }} · {{ project.roles.join(', ') }}{{ project.canDecide ? ' · decides' : '' }}</span>
          </li>
        }
      </ul>
    }`,
  styles: `.projects { list-style: none; padding: 0; } .projects li { padding: 10px 0; border-bottom: 1px solid var(--border); }
    .meta { color: var(--muted); margin-left: 12px; font-size: 0.85rem; } .error { color: var(--danger); }`,
})
export class ProjectsPage implements OnInit {
  protected readonly state = inject(WorkspaceState);
  ngOnInit(): void { void this.state.loadProjects(); }
}
