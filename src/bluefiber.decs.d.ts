declare global {
  const FEATURES_FLAGS: FEATURES_FLAGS;

  interface FEATURES_FLAGS {
    FEATURE1: string;
  }

  interface Window {
    _env: {
      websocketUrl: string;
    };
  }
}

export {};
