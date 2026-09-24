/**
 * bun:ffi 最小类型声明（仅本插件用到的 API）。
 * opencode 运行时为 Bun，运行时由 Bun 提供实现；此文件仅服务编辑器/tsc。
 */
declare module "bun:ffi" {
  export const FFIType: {
    ptr: string;
    u32: string;
    i32: string;
    f64: string;
    void: string;
  };

  export interface FFISymbolOptions {
    args: string[];
    returns: string;
  }

  export interface Library {
    symbols: Record<string, (...args: unknown[]) => unknown>;
  }

  export function dlopen(
    path: string,
    symbols: Record<string, FFISymbolOptions>,
  ): Library;
}
