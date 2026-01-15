export const getTrackDataSelector = (store) => {
  return {
    lon: store?.track?.lon || 0,
    lat: store?.track?.lat || 0,
    alt: store?.track?.alt || 0,
    speed: store?.track?.speed || 0,
    classification: store?.track?.classification,
    status: store?.track?.status,
  };
};
