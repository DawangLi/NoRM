namespace NoRM.BSON
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using Attributes;
    using DbTypes;

    public class BsonDeserializer
    {
        private static readonly Type _IEnumerableType = typeof(IEnumerable);
        
        public static T Deserialize<T>(byte[] objectData) where T : class, new()
        {
            IDictionary<WeakReference, Flyweight> outprops = new Dictionary<WeakReference, Flyweight>();
            return Deserialize<T>(objectData, ref outprops);
        }
        public static T Deserialize<T>(byte[] objectData, ref IDictionary<WeakReference, Flyweight> outProps)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(objectData, 0, objectData.Length);
                ms.Position = 0;
                return Deserialize<T>(new BinaryReader(ms), ref outProps);
            }
        }
        private static T Deserialize<T>(BinaryReader stream, ref IDictionary<WeakReference, Flyweight> outProps)
        {
            return new BsonDeserializer(stream).Read<T>(ref outProps);
        }

        private readonly BinaryReader _reader;
        private Document _current;

        private BsonDeserializer(BinaryReader reader)
        {            
            _reader = reader;             
        }

        private T Read<T>(ref IDictionary<WeakReference, Flyweight> outProps)
        {
            NewDocument(_reader.ReadInt32());            
            var @object = (T)DeserializeValue(typeof(T), BSONTypes.Object);            
            return @object;
        }

        private void Read(int read)
        {            
            _current.Read += read;
        }
        private bool IsDone()
        {
            var isDone = _current.Read + 1 == _current.Length;
            if (isDone)
            {
                _reader.ReadByte(); // EOO
                var old = _current;
                _current = old.Parent;
                if (_current != null){ Read(old.Length); }
            }
            return isDone;
        }
        private void NewDocument(int length)
        {
            var old = _current;
            _current = new Document {Length = length, Parent = old, Read = 4};            
        }
        private object DeserializeValue(Type type, BSONTypes storedType)
        {
            if (storedType == BSONTypes.Null)
            {
                return null;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {            
                type = Nullable.GetUnderlyingType(type);
            }  

            if (type == typeof(string))
            {
                return ReadString();                
            }
            if (type == typeof(int))
            {
                return ReadInt(storedType);
            }
            if (type.IsEnum)
            {
                return ReadEnum(type, storedType);
            }
            if (storedType == BSONTypes.Binary)
            {
                return ReadBinary();
            }
            if (_IEnumerableType.IsAssignableFrom(type))
            {
                return ReadList(type);
            }
            if (type == typeof(bool))
            {
                Read(1);
                return _reader.ReadBoolean();
            } 
            if (type == typeof(DateTime))
            {
                return BsonHelper.EPOCH.AddMilliseconds(ReadLong(BSONTypes.Int64));
            }
            if (type == typeof(OID))
            {
                Read(12);
                return new OID(_reader.ReadBytes(12));
            }                       
  
            if (type == typeof(long))
            {
                return ReadLong(storedType);
            }
            if (type == typeof(double))
            {
                Read(8);
                return _reader.ReadDouble();
            }
            if (type == typeof(Regex))
            {
                return ReadRegularExpression();
            }
            if (storedType == BSONTypes.ScopedCode)
            {
                //todo
                return null;
            }      
           
            return ReadObject(type);            
        }

        private object ReadObject(Type type)
        {
            var instance = Activator.CreateInstance(type);
            while (true)
            {
                var storageType = ReadType();                
                var name = ReadName();
                if (name == "$err")
                {
                    var message = DeserializeValue(typeof(string), BSONTypes.String);
                    //todo: something
                }
                if (name == "_id"){ name = "Id";  }
                var property = ReflectionHelpers.FindProperty(type, name);
                if (storageType == BSONTypes.Object)
                {
                    NewDocument(_reader.ReadInt32());
                }
                var value = DeserializeValue(property.PropertyType, storageType);
                property.SetValue(instance, value, null);
                if (IsDone())
                {
                    break;
                }
            }
            return instance;
        }
        private object ReadList(Type listType)
        {
            NewDocument(_reader.ReadInt32());
            var itemType = ListHelper.GetListItemType(listType);            
            bool isReadonly;
            var container = ListHelper.CreateContainer(listType, itemType, out isReadonly);            
            while (true)
            {
                var type = ReadType();
                ReadName();            
                var value = DeserializeValue(itemType, type);
                container.Add(value);  
                if (IsDone()) { break; }
            }
            if (listType.IsArray)
            {
                return ListHelper.ToArray((List<object>)container, itemType);
            }
            if (isReadonly)
            {
                return listType.GetConstructor(BindingFlags.Instance | BindingFlags.Public, null, new[] { container.GetType() }, null).Invoke(new object[] { container });
            }
            return container;
        }
        private object ReadBinary()
        {
            var length = _reader.ReadInt32();            
            var subType = _reader.ReadByte();
            Read(5 + length);            
            if (subType == 2)
            {                
                return _reader.ReadBytes(_reader.ReadInt32());  
            }
            if (subType == 3)
            {
                return new Guid(_reader.ReadBytes(length));
            }
            throw new MongoException("No support for binary type: " + subType);
        }
        private string ReadName()
        {
            var buffer = new List<byte>(128); //todo: use a pool to prevent fragmentation
            byte b;
            while ((b = _reader.ReadByte()) > 0)
            {
                buffer.Add(b);                
            }
            Read(buffer.Count+1);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        private string ReadString()
        {
            var length = _reader.ReadInt32();
            var buffer = _reader.ReadBytes(length - 1); //todo: again, look at fragementation prevention
            _reader.ReadByte(); //null;
            Read(4 + length);

            return Encoding.UTF8.GetString(buffer);
        }
        private int ReadInt(BSONTypes storedType)
        {
            switch(storedType)
            {
                case BSONTypes.Int32:
                    Read(4);
                    return _reader.ReadInt32();
                case BSONTypes.Int64:
                    Read(8);
                    return (int)_reader.ReadInt64();
                case BSONTypes.Double:
                    Read(8);
                    return (int)_reader.ReadDouble();    
                default:
                    throw new MongoException("todo:");                 //todo
            }
        }
        private long ReadLong(BSONTypes storedType)
        {
            switch (storedType)
            {
                case BSONTypes.Int64:
                    Read(8);
                    return _reader.ReadInt64();
                case BSONTypes.Double:
                    Read(8);
                    return (long)_reader.ReadDouble();
                default:
                    throw new MongoException("todo:");                 //todo                    
            }
        }
        private object ReadEnum(Type type, BSONTypes storedType)
        {
            if (storedType == BSONTypes.Int64)
            {
                return Enum.Parse(type, ReadLong(storedType).ToString(), false);
            }
            return Enum.Parse(type, ReadInt(storedType).ToString(), false);            
        }
        private object ReadRegularExpression()
        {
            var pattern = ReadName();
            var optionsString = ReadName();

            var options = RegexOptions.None;
            if (optionsString.Contains("e")) options = options | RegexOptions.ECMAScript;
            if (optionsString.Contains("i")) options = options | RegexOptions.IgnoreCase;
            if (optionsString.Contains("l")) options = options | RegexOptions.CultureInvariant;
            if (optionsString.Contains("m")) options = options | RegexOptions.Multiline;
            if (optionsString.Contains("s")) options = options | RegexOptions.Singleline;
            if (optionsString.Contains("w")) options = options | RegexOptions.IgnorePatternWhitespace;
            if (optionsString.Contains("x")) options = options | RegexOptions.ExplicitCapture;
            
            return new Regex(pattern, options);
        }
        private BSONTypes ReadType()
        {
            Read(1);
            return (BSONTypes)_reader.ReadByte();
        }


        private class Document
        {
            public int Length;
            public int Read;
            public Document Parent;
        }

    }
}