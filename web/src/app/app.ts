import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { IdleWatch } from './core/idle-watch';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, IdleWatch],
  template: '<router-outlet /><app-idle-watch />',
})
export class App {}
