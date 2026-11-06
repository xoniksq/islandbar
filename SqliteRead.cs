using System.Runtime.InteropServices;

namespace IslandBar;

static class SqliteRead
{
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out nint db,int flags,nint vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern int sqlite3_prepare_v2(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,int size,out nint statement,nint tail);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern int sqlite3_step(nint statement);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern nint sqlite3_column_text(nint statement,int column);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern long sqlite3_column_int64(nint statement,int column);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern int sqlite3_finalize(nint statement);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern int sqlite3_close(nint db);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern int sqlite3_busy_timeout(nint db,int ms);
    public static List<T> Query<T>(string file,string sql,Func<Row,T> convert)
    {
        nint db=0,statement=0;
        try{
            if(sqlite3_open_v2(file,out db,1,0)!=0)throw new IOException("Не удалось прочитать базу загрузок");
            sqlite3_busy_timeout(db,80);
            if(sqlite3_prepare_v2(db,sql,-1,out statement,0)!=0)throw new IOException("Формат базы загрузок не поддерживается");
            var rows=new List<T>();int result;
            while((result=sqlite3_step(statement))==100)rows.Add(convert(new Row(statement)));
            if(result!=101)throw new IOException("База загрузок занята");return rows;
        }finally{if(statement!=0)sqlite3_finalize(statement);if(db!=0)sqlite3_close(db);}
    }
    public readonly struct Row(nint statement)
    {
        public string Text(int column)=>Marshal.PtrToStringUTF8(sqlite3_column_text(statement,column))??"";
        public long Number(int column)=>sqlite3_column_int64(statement,column);
    }
}
