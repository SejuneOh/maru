// Fixed public address for the API while it runs behind a quick tunnel whose address changes on every start.
// UPSTREAM holds the current tunnel origin (https://<name>.trycloudflare.com); scripts/api-tunnel.sh updates it.

const json = (status, error) =>
  new Response(JSON.stringify({ error }), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
      "access-control-allow-origin": "*",
    },
  });

// Quick tunnel names are hyphen-joined words; this also rules out api.trycloudflare.com, the issuing endpoint.
const QUICK_TUNNEL = /^https:\/\/[a-z0-9]+(-[a-z0-9]+)+\.trycloudflare\.com$/;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== "/api" && !url.pathname.startsWith("/api/")) return json(404, "not_found");

    const upstream = (env.UPSTREAM || "").trim().replace(/\/+$/, "");
    if (!QUICK_TUNNEL.test(upstream)) return json(503, "api_offline");

    const target = upstream + url.pathname + url.search;
    try {
      return await fetch(new Request(target, request), { redirect: "manual" });
    } catch {
      return json(503, "api_offline");
    }
  },
};
