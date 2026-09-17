const exposes = {
  web: {
    // ./PageAdmin 名稱固定，為管理者端進入點
    './PageAdmin': './src/app/web/pages/admin/admin.module.ts',
    // ./PageUser 名稱固定，為使用者端進入點
    './PageUser': './src/app/web/pages/user/user.module.ts',
  },
  app: {
    // ./Page 名稱固定，為手機端進入點
    './Page': './src/app/mobile/pages/page.module.ts',
    // App 端面板
    './Panel/App': './src/app/mobile/panels/app-panel.module.ts',
  }
};

module.exports = exposes;
