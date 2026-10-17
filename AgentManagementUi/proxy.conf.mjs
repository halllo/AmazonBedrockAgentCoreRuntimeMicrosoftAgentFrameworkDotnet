// Dev-server proxy: /api/* -> AgentManagement (no CORS needed). Aspire injects the service URL.
const target =
  process.env['services__agent-management__http__0'] ??
  process.env['AGENT_MANAGEMENT_HTTP'] ??
  'http://localhost:5080';

export default {
  '/api': {
    target,
    changeOrigin: true,
    pathRewrite: { '^/api': '' },
  },
};
