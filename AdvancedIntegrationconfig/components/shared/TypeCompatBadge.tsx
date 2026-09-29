import * as React from "react";
import { Icon, TooltipHost } from "@fluentui/react";
import { compareTypes } from "../../types";


export const TypeCompatBadge: React.FC<{
  sourceType?: string | null;
  destType?: string | null;
}> = ({ sourceType, destType }) => {
  if (!sourceType || !destType) return null;
  const status = compareTypes(sourceType, destType);

  if (status === "exact") {
    return (
      <TooltipHost content={`Exact type match (${sourceType})`}>
        <Icon iconName="CheckMark" styles={{ root: { color: "#107c10" } }} />
      </TooltipHost>
    );
  }
  if (status === "compatible") {
    return (
      <TooltipHost content={`Compatible types: ${sourceType} → ${destType}`}>
        <Icon iconName="Info" styles={{ root: { color: "#605e5c" } }} />
      </TooltipHost>
    );
  }
  return (
    <TooltipHost content={`Type mismatch: ${sourceType} → ${destType}. Verify a conversion formula handles this.`}>
      <Icon iconName="WarningSolid" styles={{ root: { color: "#d29200" } }} />
    </TooltipHost>
  );
};
