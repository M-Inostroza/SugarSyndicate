using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(GoalManager.BonusObjective))]
public class GoalManagerBonusObjectiveDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        float lineHeight = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        var foldoutRect = new Rect(position.x, position.y, position.width, lineHeight);
        property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);

        if (property.isExpanded)
        {
            EditorGUI.indentLevel++;

            var typeProp = property.FindPropertyRelative("type");
            var extraProp = property.FindPropertyRelative("extraItemCount");
            var daysProp = property.FindPropertyRelative("maxDays");

            var line = new Rect(position.x, position.y + lineHeight + spacing, position.width, lineHeight);
            EditorGUI.PropertyField(line, typeProp);

            line.y += lineHeight + spacing;
            var typeName = typeProp.enumNames[typeProp.enumValueIndex];
            if (typeName == "DeliverExtraItems")
                EditorGUI.PropertyField(line, extraProp);
            else if (typeName == "FinishUnderDays")
                EditorGUI.PropertyField(line, daysProp);

            EditorGUI.indentLevel--;
        }

        EditorGUI.EndProperty();
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float lineHeight = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        if (!property.isExpanded)
            return lineHeight;
        return lineHeight + (lineHeight + spacing) * 2;
    }
}
