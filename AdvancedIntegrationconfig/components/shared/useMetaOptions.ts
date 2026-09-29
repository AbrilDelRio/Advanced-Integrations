import * as React from "react";
import { getOptionSet, OptionMeta } from "../../services/metadataService";


export function useMetaOptions(entity: string, attribute: string): OptionMeta[] | null {
  const [options, setOptions] = React.useState<OptionMeta[] | null>(null);
  React.useEffect(() => {
    let cancelled = false;
    setOptions(null);
    getOptionSet(entity, attribute)
      .then(o => { if (!cancelled) setOptions(o); })
      .catch(() => { if (!cancelled) setOptions([]); });
    return () => { cancelled = true; };
  }, [entity, attribute]);
  return options;
}


export function labelImpliesNoValue(label?: string | null): boolean {
  if (!label) return false;
  return /\bis\s*(not\s*)?(null|empty|blank)\b/i.test(label);
}


export function labelImpliesLookup(label?: string | null): boolean {
  if (!label) return false;
  return /lookup|customer|owner/i.test(label);
}

export function labelFor(options: OptionMeta[] | null, value?: number | null): string | undefined {
  if (!options || value === null || value === undefined) return undefined;
  return options.find(o => o.value === value)?.label;
}
