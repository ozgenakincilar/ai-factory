import { Routes } from '@angular/router';
import { ProjectPage } from './project.page';
import { ProjectsPage } from './projects.page';
import { ReviewPage } from './review.page';
import { RunPage } from './run.page';

export const routes: Routes = [
  { path: '', component: ProjectsPage, title: 'Projects' },
  { path: 'projects/:id', component: ProjectPage, title: 'Project' },
  { path: 'projects/:id/runs/:runId', component: RunPage, title: 'Run' },
  { path: 'projects/:id/reviews/:caseId', component: ReviewPage, title: 'Review' },
  { path: '**', redirectTo: '' },
];
