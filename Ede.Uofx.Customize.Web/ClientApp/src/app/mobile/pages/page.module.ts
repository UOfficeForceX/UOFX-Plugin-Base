import { CommonModule } from '@angular/common';
import { IonicModule } from '@ionic/angular';
import { NgModule } from '@angular/core';
import { RouterModule } from '@angular/router';
import { UofxModalModule } from '@uofx/app-components/modal';

const PACKAGES = [
  UofxModalModule,
];
const COMPONENTS = [];

@NgModule({
  imports: [
    CommonModule,
    IonicModule.forRoot(),
    RouterModule.forChild([]),

    ...PACKAGES,
  ],
  exports: [...COMPONENTS],
  declarations: [...COMPONENTS],
})
export class PageModule { }
