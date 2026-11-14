/**
 * @fileoverview Tests for the assistant attachment helper (client-side pre-checks
 * and base64 encoding). The server re-validates; these cover the UX guards.
 */

import {
  ACCEPT_ATTR,
  attachmentToPart,
  fileToAttachment,
  isImageType,
  MAX_FILE_BYTES,
  resolveMime,
  validateFile,
} from '../attachments';

const makeFile = (name: string, type: string, size = 10): File => {
  const blob = new Blob([new Uint8Array(size)], { type });
  return new File([blob], name, { type });
};

// jsdom's FileReader rejects the jest-polyfilled File (cross-realm Blob identity),
// so stub it: it only needs to hand back a data: URL for readBase64 to strip.
class FakeFileReader {
  public result: string | null = null;
  public onload: (() => void) | null = null;
  public onerror: (() => void) | null = null;
  readAsDataURL(): void {
    this.result = 'data:application/octet-stream;base64,AAAABBBBCCCC==';
    setTimeout(() => this.onload?.(), 0);
  }
}

beforeAll(() => {
  // jsdom has no object URLs
  Object.defineProperty(URL, 'createObjectURL', { value: () => 'blob:mock', writable: true });
  Object.defineProperty(URL, 'revokeObjectURL', { value: () => undefined, writable: true });
  (globalThis as unknown as { FileReader: unknown }).FileReader = FakeFileReader;
});

describe('resolveMime', () => {
  it('uses a valid browser MIME type', () => {
    expect(resolveMime(makeFile('a.png', 'image/png'))).toBe('image/png');
  });

  it('falls back to the extension when the type is missing', () => {
    expect(resolveMime(makeFile('notes.md', ''))).toBe('text/markdown');
    expect(resolveMime(makeFile('data.json', ''))).toBe('application/json');
    expect(resolveMime(makeFile('sheet.csv', ''))).toBe('text/csv');
  });

  it('returns null for unsupported files', () => {
    expect(resolveMime(makeFile('evil.exe', 'application/x-msdownload'))).toBeNull();
    expect(resolveMime(makeFile('archive.zip', 'application/zip'))).toBeNull();
  });
});

describe('validateFile', () => {
  it('accepts a supported file', () => {
    expect(validateFile(makeFile('a.pdf', 'application/pdf'))).toBeNull();
  });

  it('rejects an unsupported type', () => {
    expect(validateFile(makeFile('x.exe', 'application/x-msdownload'))).toMatch(/not a supported/);
  });

  it('rejects an empty file', () => {
    expect(validateFile(makeFile('e.txt', 'text/plain', 0))).toMatch(/empty/);
  });

  it('rejects a file over the per-file cap', () => {
    const big = makeFile('big.png', 'image/png', MAX_FILE_BYTES + 1);
    expect(validateFile(big)).toMatch(/larger than/);
  });
});

describe('isImageType', () => {
  it('is true for raster images and false for svg/documents', () => {
    expect(isImageType('image/png')).toBe(true);
    expect(isImageType('image/svg+xml')).toBe(false);
    expect(isImageType('application/pdf')).toBe(false);
  });
});

describe('fileToAttachment', () => {
  it('reads base64 without the data: prefix and sets a preview for images', async () => {
    const a = await fileToAttachment(makeFile('a.png', 'image/png', 4));
    expect(a.mimeType).toBe('image/png');
    expect(a.data).not.toContain(',');
    expect(a.data).not.toMatch(/^data:/);
    expect(a.previewUrl).toBe('blob:mock');
  });

  it('does not set a preview for non-images', async () => {
    const a = await fileToAttachment(makeFile('a.pdf', 'application/pdf', 4));
    expect(a.previewUrl).toBeUndefined();
  });
});

describe('attachmentToPart', () => {
  it('produces an inlineData part', () => {
    const part = attachmentToPart({
      id: '1',
      name: 'a.png',
      mimeType: 'image/png',
      size: 4,
      data: 'AAAA',
    });
    expect(part).toEqual({ inlineData: { mimeType: 'image/png', data: 'AAAA' } });
  });
});

describe('ACCEPT_ATTR', () => {
  it('includes common MIME types and extensions', () => {
    expect(ACCEPT_ATTR).toContain('application/pdf');
    expect(ACCEPT_ATTR).toContain('.docx');
    expect(ACCEPT_ATTR).toContain('.png');
  });
});
