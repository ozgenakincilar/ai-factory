import { Routes } from '@angular/router';
import { DevelopmentPage } from './development.page';
import { ProjectPage } from './project.page';
import { ProjectsPage } from './projects.page';
import { ReviewPage } from './review.page';
import { RunPage } from './run.page';
import { VerificationPage } from './verification.page';

export const routes: Routes = [
  { path: '', component: ProjectsPage, title: 'Projects' },
  { path: 'projects/:id', component: ProjectPage, title: 'Project' },
  { path: 'projects/:id/runs/:runId', component: RunPage, title: 'Run' },
  { path: 'projects/:id/reviews/:caseId', component: ReviewPage, title: 'Review' },
  { path: 'projects/:id/verifications/:jobId', component: VerificationPage, title: 'Verification' },
  { path: 'projects/:id/developments/:jobId', component: DevelopmentPage, title: 'Development' },
  { path: '**', redirectTo: '' },
];
