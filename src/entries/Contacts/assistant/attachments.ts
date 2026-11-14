/**
 * @fileoverview Turns files the user attaches to the assistant into Gemini inline
 * parts. These checks are only for quick feedback; the backend re-validates every
 * file (type, magic bytes, size) and is the real gate.
 * @module Contacts/assistant/attachments
 */

import type { GeminiPart } from '../../../services/api/BackendApiClient';

/** Largest single file (mirrors the server's default) */
export const MAX_FILE_BYTES = 10 * 1024 * 1024;

/** Largest total across one message (mirrors the server's default; stays under nginx's 20MB) */
export const MAX_TOTAL_BYTES = 13 * 1024 * 1024;

/** Most files in one message (mirrors the server's default) */
export const MAX_FILES = 5;

/** Accepted MIME types, mirroring the backend allowlist (the server enforces it) */
export const ALLOWED_MIME: Record<string, { label: string; ext: string[] }> = {
  'image/png': { label: 'PNG image', ext: ['.png'] },
  'image/jpeg': { label: 'JPEG image', ext: ['.jpg', '.jpeg'] },
  'image/webp': { label: 'WebP image', ext: ['.webp'] },
  'image/gif': { label: 'GIF image', ext: ['.gif'] },
  'image/heic': { label: 'HEIC image', ext: ['.heic'] },
  'image/heif': { label: 'HEIF image', ext: ['.heif'] },
  'application/pdf': { label: 'PDF', ext: ['.pdf'] },
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': {
    label: 'Word document',
    ext: ['.docx'],
  },
  'application/msword': { label: 'Word document', ext: ['.doc'] },
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': {
    label: 'Excel workbook',
    ext: ['.xlsx'],
  },
  'application/vnd.ms-excel': { label: 'Excel workbook', ext: ['.xls'] },
  'application/vnd.openxmlformats-officedocument.presentationml.presentation': {
    label: 'PowerPoint',
    ext: ['.pptx'],
  },
  'application/vnd.ms-powerpoint': { label: 'PowerPoint', ext: ['.ppt'] },
  'application/vnd.oasis.opendocument.text': { label: 'OpenDocument text', ext: ['.odt'] },
  'application/vnd.oasis.opendocument.spreadsheet': {
    label: 'OpenDocument sheet',
    ext: ['.ods'],
  },
  'application/vnd.oasis.opendocument.presentation': {
    label: 'OpenDocument slides',
    ext: ['.odp'],
  },
  'text/plain': { label: 'text file', ext: ['.txt'] },
  'text/markdown': { label: 'Markdown', ext: ['.md', '.markdown'] },
  'text/csv': { label: 'CSV', ext: ['.csv'] },
  'text/html': { label: 'HTML', ext: ['.html', '.htm'] },
  'text/xml': { label: 'XML', ext: ['.xml'] },
  'application/xml': { label: 'XML', ext: ['.xml'] },
  'application/json': { label: 'JSON', ext: ['.json'] },
};

/** The `accept` attribute for the file input: every allowed MIME type and extension */
export const ACCEPT_ATTR = Array.from(
  new Set([...Object.keys(ALLOWED_MIME), ...Object.values(ALLOWED_MIME).flatMap((v) => v.ext)]),
).join(',');

/** A file ready to send */
export interface Attachment {
  /** Local id for the UI */
  id: string;
  /** File name, for the chip */
  name: string;
  /** Validated MIME type */
  mimeType: string;
  /** Size in bytes */
  size: number;
  /** Base64 of the file (no data: prefix) */
  data: string;
  /** Object URL for a thumbnail, images only (revoke when removed) */
  previewUrl?: string;
}

/** True for a type we show as a thumbnail (raster images only — never SVG/HTML) */
export const isImageType = (mime: string): boolean =>
  mime.startsWith('image/') && mime !== 'image/svg+xml';

const extensionOf = (name: string): string => {
  const dot = name.lastIndexOf('.');
  return dot >= 0 ? name.slice(dot).toLowerCase() : '';
};

/**
 * The MIME type to use for a file: the browser's type when it is on the allowlist,
 * otherwise a type inferred from the extension (browsers often leave .md, .csv or
 * .json with an empty or odd type).
 * @param file - The chosen file
 * @returns An allowed MIME type, or null when the file is not supported
 */
export const resolveMime = (file: File): string | null => {
  const t = (file.type || '').split(';')[0].trim().toLowerCase();
  if (t && ALLOWED_MIME[t]) {
    return t;
  }
  const ext = extensionOf(file.name);
  const match = Object.entries(ALLOWED_MIME).find(([, v]) => v.ext.includes(ext));
  return match ? match[0] : null;
};

/**
 * Checks one file before it is read.
 * @param file - The chosen file
 * @returns An error message, or null when the file is acceptable
 */
export const validateFile = (file: File): string | null => {
  const mime = resolveMime(file);
  if (!mime) {
    return `"${file.name}" is not a supported file type.`;
  }
  if (file.size === 0) {
    return `"${file.name}" is empty.`;
  }
  if (file.size > MAX_FILE_BYTES) {
    return `"${file.name}" is larger than ${MAX_FILE_BYTES / (1024 * 1024)} MB.`;
  }
  return null;
};

const readBase64 = (file: File): Promise<string> =>
  new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error(`Could not read "${file.name}".`));
    reader.onload = () => {
      const result = String(reader.result ?? '');
      const comma = result.indexOf(',');
      resolve(comma >= 0 ? result.slice(comma + 1) : result);
    };
    reader.readAsDataURL(file);
  });

/**
 * Reads a validated file into an attachment.
 * @param file - A file that passed {@link validateFile}
 * @returns The attachment, with a preview URL for images
 */
export const fileToAttachment = async (file: File): Promise<Attachment> => {
  const mimeType = resolveMime(file) ?? 'application/octet-stream';
  const data = await readBase64(file);
  return {
    id: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
    name: file.name,
    mimeType,
    size: file.size,
    data,
    previewUrl: isImageType(mimeType) ? URL.createObjectURL(file) : undefined,
  };
};

/** The Gemini part for an attachment */
export const attachmentToPart = (a: Attachment): GeminiPart => ({
  inlineData: { mimeType: a.mimeType, data: a.data },
});
