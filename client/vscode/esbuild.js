const esbuild = require('esbuild');

// 单文件 bundle：vsix 里不携带 node_modules（详设 11 的离线自足目标）。
const options = {
  entryPoints: ['src/extension.ts'],
  bundle: true,
  outfile: 'dist/extension.js',
  external: ['vscode'],
  platform: 'node',
  format: 'cjs',
  target: 'node18',
  sourcemap: true,
  minify: false,
};

if (process.argv.includes('--watch')) {
  esbuild.context(options).then((ctx) => ctx.watch());
} else {
  esbuild.build(options).catch(() => process.exit(1));
}