import { CUSTOM_ELEMENTS_SCHEMA, NO_ERRORS_SCHEMA, NgModule } from '@angular/core';

import { AppComponent } from './app.component';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { BrowserModule } from '@angular/platform-browser';
import { CheckPluginComponent } from '@uofx/plugin';
import { FormsModule } from '@angular/forms';
import { Helper } from '@uofx/core';
import { HttpClientModule } from '@angular/common/http';
import { IconModule } from './icon.module';
import { MessageService } from 'primeng/api';
import { RouterModule } from '@angular/router';
import { UofxPackageModule } from './uofx-pack.module';

//#endregion

@NgModule({
  declarations: [
    AppComponent,
  ],
  imports: [
    BrowserModule,
    BrowserAnimationsModule,
    HttpClientModule,
    FormsModule,
    RouterModule.forRoot([
      { path: '', component: CheckPluginComponent, pathMatch: 'full' },
    ]),
    IconModule.forRoot(),
    UofxPackageModule,
  ],
  providers: [
    { provide: 'BASE_HREF', useFactory: Helper.getBaseHref },
    MessageService,
  ],
  bootstrap: [AppComponent],
  schemas: [
    CUSTOM_ELEMENTS_SCHEMA,
    NO_ERRORS_SCHEMA
  ]
})
export class AppModule { }
