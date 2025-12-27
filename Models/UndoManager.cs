using System.Collections.Generic;

namespace _3MFTool.Models;

/// <summary>
/// Lightweight undo/redo system that stores only changed triangles per action.
/// Memory efficient - doesn't store full mesh state.
/// </summary>
public class UndoManager
{
    private readonly Stack<PaintAction> _undoStack = new();
    private readonly Stack<PaintAction> _redoStack = new();
    private readonly int _maxHistory;
    
    // Temporary state for current stroke
    private Dictionary<int, List<SubTriangle>>? _strokeStartState;
    private HashSet<int>? _modifiedTriangles;
    private Mesh? _mesh;

    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event Action? StateChanged;

    public UndoManager(int maxHistory = 50)
    {
        _maxHistory = maxHistory;
    }

    public void SetMesh(Mesh? mesh)
    {
        _mesh = mesh;
        Clear();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _strokeStartState = null;
        _modifiedTriangles = null;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Call at the start of a paint stroke to begin capturing state
    /// </summary>
    public void BeginStroke()
    {
        if (_mesh == null) return;
        _strokeStartState = new Dictionary<int, List<SubTriangle>>();
        _modifiedTriangles = new HashSet<int>();
    }

    /// <summary>
    /// Call when a triangle is about to be modified (before ApplyPaint)
    /// </summary>
    public void MarkTriangleModified(int triangleIndex)
    {
        if (_mesh == null || _strokeStartState == null || _modifiedTriangles == null) return;
        if (triangleIndex < 0 || triangleIndex >= _mesh.Triangles.Count) return;
        
        // Only capture initial state once per triangle per stroke
        if (_modifiedTriangles.Add(triangleIndex))
        {
            // Deep copy the PaintData before modification
            var tri = _mesh.Triangles[triangleIndex];
            _strokeStartState[triangleIndex] = CopyPaintData(tri.PaintData);
        }
    }

    /// <summary>
    /// Call at the end of a paint stroke to commit the action
    /// </summary>
    public void EndStroke()
    {
        if (_mesh == null || _strokeStartState == null || _modifiedTriangles == null) return;
        if (_modifiedTriangles.Count == 0)
        {
            _strokeStartState = null;
            _modifiedTriangles = null;
            return;
        }

        // Capture final state of all modified triangles
        var newState = new Dictionary<int, List<SubTriangle>>();
        foreach (int triIdx in _modifiedTriangles)
        {
            var tri = _mesh.Triangles[triIdx];
            newState[triIdx] = CopyPaintData(tri.PaintData);
        }

        // Create action with before/after state
        var action = new PaintAction(_strokeStartState, newState);
        
        // Push to undo stack
        _undoStack.Push(action);
        
        // Clear redo stack (new action invalidates redo history)
        _redoStack.Clear();
        
        // Trim history if too large
        TrimHistory();

        _strokeStartState = null;
        _modifiedTriangles = null;
        
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Cancel current stroke without creating undo entry
    /// </summary>
    public void CancelStroke()
    {
        _strokeStartState = null;
        _modifiedTriangles = null;
    }

    /// <summary>
    /// Undo the last paint action
    /// </summary>
    public bool Undo()
    {
        if (_mesh == null || _undoStack.Count == 0) return false;

        var action = _undoStack.Pop();
        
        // Restore old state
        foreach (var kvp in action.OldState)
        {
            int triIdx = kvp.Key;
            if (triIdx >= 0 && triIdx < _mesh.Triangles.Count)
            {
                _mesh.Triangles[triIdx].PaintData = CopyPaintData(kvp.Value);
            }
        }

        _redoStack.Push(action);
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Redo the last undone action
    /// </summary>
    public bool Redo()
    {
        if (_mesh == null || _redoStack.Count == 0) return false;

        var action = _redoStack.Pop();
        
        // Apply new state
        foreach (var kvp in action.NewState)
        {
            int triIdx = kvp.Key;
            if (triIdx >= 0 && triIdx < _mesh.Triangles.Count)
            {
                _mesh.Triangles[triIdx].PaintData = CopyPaintData(kvp.Value);
            }
        }

        _undoStack.Push(action);
        StateChanged?.Invoke();
        return true;
    }

    private void TrimHistory()
    {
        // Remove oldest entries if over limit
        if (_undoStack.Count > _maxHistory)
        {
            var temp = new Stack<PaintAction>();
            int keep = _maxHistory;
            while (_undoStack.Count > 0 && keep > 0)
            {
                temp.Push(_undoStack.Pop());
                keep--;
            }
            _undoStack.Clear();
            while (temp.Count > 0)
            {
                _undoStack.Push(temp.Pop());
            }
        }
    }

    /// <summary>
    /// Deep copy PaintData list
    /// </summary>
    private static List<SubTriangle> CopyPaintData(List<SubTriangle> source)
    {
        var copy = new List<SubTriangle>(source.Count);
        foreach (var sub in source)
        {
            // SubTriangle stores BaryCorners as array - need to copy it
            var baryClone = new BarycentricCoord[3];
            baryClone[0] = sub.BaryCorners[0];
            baryClone[1] = sub.BaryCorners[1];
            baryClone[2] = sub.BaryCorners[2];
            copy.Add(new SubTriangle(baryClone, sub.ExtruderId, sub.Depth, sub.Masked));
        }
        return copy;
    }
}

/// <summary>
/// Represents a single undoable paint action.
/// Stores only the triangles that were modified.
/// </summary>
public class PaintAction
{
    public Dictionary<int, List<SubTriangle>> OldState { get; }
    public Dictionary<int, List<SubTriangle>> NewState { get; }

    public PaintAction(
        Dictionary<int, List<SubTriangle>> oldState,
        Dictionary<int, List<SubTriangle>> newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    /// <summary>
    /// Approximate memory usage in bytes
    /// </summary>
    public int ApproximateSize
    {
        get
        {
            int count = 0;
            foreach (var list in OldState.Values)
                count += list.Count;
            foreach (var list in NewState.Values)
                count += list.Count;
            // Each SubTriangle: 3 BaryCoords (12 bytes each) + int + int + bool ≈ 48 bytes
            return count * 48;
        }
    }
}
