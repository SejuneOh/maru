// Calls the API health check on a schedule. A Render free web service sleeps after 15 minutes without
// requests, which would stop GitHub sync and delay webhooks, so this keeps it awake.

export default {
  async scheduled(_event, env, ctx) {
    ctx.waitUntil(fetch(`${env.TARGET}/api/health`, { headers: { "user-agent": "maru-keepalive" } }));
  },
};
