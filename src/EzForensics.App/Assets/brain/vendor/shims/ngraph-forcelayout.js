// Shim for 'ngraph.forcelayout' (EzForensics Brain). The Brain uses the d3 force engine only; the ngraph engine is not shipped.
export default function createLayout() {
  throw new Error('The ngraph layout engine is not included in the Brain page.');
}
