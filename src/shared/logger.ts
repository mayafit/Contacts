/**
 * @fileoverview Structured JSON logger for observability
 * @module shared/logger
 */

export enum LogLevel {
  DEBUG = 'debug',
  INFO = 'info',
  WARN = 'warn',
  ERROR = 'error',
}

export interface LogContext {
  /** Module or component context */
  context: string;
  /** OpenTelemetry trace ID */
  traceID?: string;
  /** Additional metadata */
  metadata?: Record<string, unknown>;
}

export interface LogEntry extends LogContext {
  /** Log level */
  level: LogLevel;
  /** Log message */
  message: string;
  /** ISO 8601 timestamp */
  timestamp: string;
  /** Error stack trace if applicable */
  stack?: string;
}

class Logger {
  private log(level: LogLevel, context: LogContext, message: string, error?: Error): void {
    const entry: LogEntry = {
      level,
      context: context.context,
      traceID: context.traceID,
      metadata: context.metadata,
      message,
      timestamp: new Date().toISOString(),
    };

    if (error) {
      entry.stack = error.stack;
      if (context.metadata) {
        context.metadata.errorMessage = error.message;
        context.metadata.errorName = error.name;
      } else {
        entry.metadata = {
          errorMessage: error.message,
          errorName: error.name,
        };
      }
    }

    // Output structured JSON log
    const logMessage = JSON.stringify(entry);

    switch (level) {
      case LogLevel.DEBUG:
        console.debug(logMessage);
        break;
      case LogLevel.INFO:
        console.info(logMessage);
        break;
      case LogLevel.WARN:
        console.warn(logMessage);
        break;
      case LogLevel.ERROR:
        console.error(logMessage);
        break;
    }
  }

  debug(context: LogContext, message: string): void {
    this.log(LogLevel.DEBUG, context, message);
  }

  info(context: LogContext, message: string): void {
    this.log(LogLevel.INFO, context, message);
  }

  warn(context: LogContext, message: string, error?: Error): void {
    this.log(LogLevel.WARN, context, message, error);
  }

  error(context: LogContext, message: string, error?: Error): void {
    this.log(LogLevel.ERROR, context, message, error);
  }
}

export const logger = new Logger();
