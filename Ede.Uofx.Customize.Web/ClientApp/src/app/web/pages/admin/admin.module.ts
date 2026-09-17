import { FormsModule, ReactiveFormsModule } from '@angular/forms';

import { CommonModule } from '@angular/common';
import { MenuModule } from 'primeng/menu';
import { NgModule } from '@angular/core';
import { RouterModule } from '@angular/router';
import { TableModule } from 'primeng/table';
import { UofxBreadcrumbModule } from '@uofx/web-components/breadcrumb';
import { UofxButtonModule } from '@uofx/web-components/button';
import { UofxIconModule } from '@uofx/web-components/icon';
import { UofxSearchBarModule } from '@uofx/web-components/search-bar';

@NgModule({
  imports: [
    // admin/plugin/edesampleplugin/container
    // admin/plugin/edesampleplugin/sub
    // admin/plugin/edesampleplugin/sub/sider
    RouterModule.forChild([]),
    UofxBreadcrumbModule,
    UofxButtonModule,
    UofxSearchBarModule,
    UofxIconModule,

    CommonModule,
    FormsModule,
    ReactiveFormsModule,

    MenuModule,
    TableModule,
  ],
  exports: [],
  declarations: []
})
export class AdminModule { }
